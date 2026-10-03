---
title: CAESAR II → viewer (usage)
status: draft
created: 2026-10-03
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# CAESAR II → viewer

A CAESAR II pipe-stress model becomes a Pluto viewer model, `<base>.bin` + `<base>.features.json`, on the production machine: Windows PowerShell 5.1, C# 5 through `Config.ps1`, with no CAESAR II and no Python needed. This is branch `pipestress`.

**Stage 1 (2026-10-03) is geometry only:** the whole pipe with its components and one marker group per restraint combination. Results come from the Excel output in stage 2.

## Run

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Run-Caesar.ps1 `
  -Neutral "<project dir>\job.cii" -Out "<project dir>\viewer\job"
```

- **`-Out <base>`** writes `<base>.bin` and `<base>.features.json`.
  - Default: beside the `.cii`, with spaces turned to underscores.
  - An existing folder, or any path ending in `\` or `/`, means `<folder>\<name>`.
  - **Output inside the Pluto repo is refused.** Project models never land here.
- **`-ModelId`**: default `caesar/<name>`.
- **`-ArcStepDeg`**: bend chord step, default 7.5°, kept within 0.5 to 45°.
- **`-Results`** is accepted, but in stage 1 it only produces a warning.
- **Output:** progress lines start with `[step]`; the last line is `RESULT {json}` (paths, counts, warnings). The export refuses a neutral file it could not read cleanly (a VERSION line that is not a number, or an element count that differs from CONTROL) and writes nothing.
- **Open it:** pick the `.bin` and the `.features.json` together, or use `viewer/index.html?bin=…&features=…`. The groups and support markers live in the sidecar. CAESAR models are Y-up, and the file says so (META `upAxis`), so the viewer opens them Y-up without changing your Z-up setting.

From C#: `CaesarToPluto.Export(cii, resultsOrNull, outBase, modelId[, CaesarOptions])` returns a `CaesarExportResult` with `Summary()`, `ToJson()` and `Warnings`.

## What the neutral file becomes

Code: `scripts/arms/CaesarNeutralReader.cs` → `CaesarGeometry.cs` → `CaesarToPluto.cs`.

> [!info]- Reading the `.cii` (CaesarNeutralReader)
> - **Layout by structure, not by version.**
>   - Lines per record = section lines ÷ the CONTROL count.
>   - ELEMENTS: the real rows, then text rows, then the last 3 non-blank rows are the pointers.
>   - RESTRANT: slots found by structure.
>   - Checked on 15.01 (the fixture) and on four public 11.00 files. Block sizes differ between those versions: ALLOWBLS 26 → 28 lines, SIF&TEES 10 → 14, REDUCERS 1 → 2.
> - **Numbers** are 13-character columns after a 2-character indent, and can touch (`3.444566E+02-3.178331E+00`). Integers are written as reals.
> - **Text rows** are a 12-character length field, a space, then the text.
> - **Line endings:** LF, CRLF, CR, CR CR LF and BOM all read the same.
> - **Sections read:**
>   - UNITS: 22 factors plus labels; length unit from the label.
>   - COORDS: in compound-length units, converted.
>   - IZUP (CONTROL row 3), NODENAME, BEND, RIGID, EXPJT, RESTRANT, DISPLMNT, REDUCERS, SIF&TEES.
>   - MISCEL_1 hangers and nozzles: layout confirmed on 11.00 only.
> - **Restraint type codes:**
>   - Confirmed: 1 ANC, 2–4 X/Y/Z, 5–7 RX/RY/RZ, 8 GUI, 9 LIM, 14/15 +Y/+Z.
>   - Every other code becomes `OTHER(n)` with a warning, until a file proves it.
>   - Rigid types: 0 Unspecified, 1 Valve, 2 Flange, 3 Flange Pair, 4 Flange Valve.
> - **Bad input** gives a warning, never an exception. Malformed-input runs: 940, with 0 exceptions and 0 NaN positions.

> [!info]- Geometry (CaesarGeometry)
> - **Node positions:**
>   - A breadth-first walk over the elements, both directions, seeded from COORDS.
>   - Restraint CNODE links and nozzle-vessel links carry the walk on: the fixture's runs connect only through them.
>   - Bend intermediate nodes seed elements that start on an elbow (trunnions, dummy legs).
>   - Anything still unreached starts at the origin, with a warning. Loop-closure misses are reported.
> - **Bends (CAESAR convention):**
>   - The deltas of the element carrying the bend run to the tangent intersection point.
>   - Only that element's TO node moves to the far point; downstream nodes are still measured from the tangent intersection point.
>   - Bend-node angles count from the near point (−2.0202 = midpoint).
>   - Arcs are drawn as chords of ≤ `ArcStepDeg`. Mitred bends are a polyline through the cut points.
> - **Bend guards:** zero-length elements, bends under 1° or over 179°, and overlapping tangents. CAESAR's 1 % attachment tolerance leaves tiny backward pieces, which are flagged.
> - **Synthetic nodes** (near points, arc vertices, valve mids) have ids from 10,000,000. Each records its two bracketing real nodes, so results can be interpolated in stage 2. Use `IsSynthetic`, not the id range.
> - **Checked on the fixture** against CAESAR's own COORDINATE REPORT in the input echo:
>   - all 128 rows within 0.0016 in;
>   - the report prints the tangent intersection point for a bend's TO node.
>
>   The far points were checked against CAESAR's displacements of the neighbouring nodes. Every arc vertex lies at radius R (1.5e-13 relative).

> [!info]- The viewer model (CaesarToPluto)
> - **Binary:**
>   - Beams only. Node ids are CAESAR node numbers.
>   - Beam id = element ordinal × 100 + piece. LABL = `FROM-TO` plus the element name, `arc k/n` or `valve a/b`.
>   - Geometry only (`Write(false)`).
>   - META `upAxis` comes from IZUP.
>   - Recentred only above 1e6 file units, into `model.units.worldOffset`.
> - **Sections:**
>   - Real pipe: `PIPE(od, wall)` per size.
>   - Display sizes for the rest, as multiples of the OD, set in `CaesarOptions`:
>     - valve: a bow-tie of two tapered halves, 1.5 → 0.6 → 1.5 × OD;
>     - flange / flange pair: 1.6 × OD;
>     - unspecified rigid: 1.15 × OD;
>     - a weightless rigid off the line: a 0.25 × OD link;
>     - expansion joint: 1.3 × OD.
>   - Reducers taper ([[vault/decisions/2026-10-03-beam-taper|ELEM slot 4]]).
> - **Sidecar groups** (tag `caesar`, merged on re-export like `SapToPluto`, so your colour and visibility edits, own groups and section cuts survive):
>   1. pipe sizes on the blue → red ramp;
>   2. components painting over them: Valve, Flange, Flange pair, Rigid, Rigid link, Expansion joint, Reducer, and Bend (hidden);
>   3. line numbers (hidden);
>   4. SIF / tee node groups (hidden);
>   5. **one node group per restraint combination at a node**, e.g. `+Y + GUI`, `ANC`, `Y w/gap + Z w/gap`, drawn as markers. They show without Color by groups, so they can sit over a contour.
> - **Fixture result:**
>   - 245 beams (95 pipe, 132 bend chords, 3 reducer, 4 valve halves, 4 flange, 5 flange pair, 2 expansion joint);
>   - 6 tapered;
>   - 13 support combinations at 45 nodes;
>   - 25 groups.

## Checks

- **Readback:** `node viewer/tests/readback_caesar.js <base> [--expect <RESULT json>]`. It checks structure, tapers against reducers and valves, `upAxis`, units, the hash binding and groups; it exits 1 on failure.
- **Fixture:** a generic job in `scripts/caesar/testdata/` (README there, with the scrub checklist).
- **On the production machine, before the first real job:** dot-source `scripts\lib\Config.ps1` in a Windows PowerShell 5.1 `-NonInteractive` session (the compile check), then run the runner on the fixture. Write the output outside the repo.

## Next

- **Stage 2:** read the Excel output, one report per tab.
  - Displacements per load case on the beams (DX DY DZ |D| RX RY RZ), interpolated at synthetic nodes, with a coverage gate.
  - Restraint loads as beam components of kind `restraint`.
  - `supports` items in the sidecar and a `supports.js` symbol layer ([[vault/decisions/2026-10-03-imported-restraints|decision]]).
  - Local element forces and B31.1 stresses (also in the workbook) can contour the pipe.
- **Stage 3:**
  - mitred ring joins at bends (removes the chord seams);
  - hangers, nozzles and CNODE lines;
  - coupling recognition by name.
