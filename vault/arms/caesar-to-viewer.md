---
title: CAESAR II → viewer (usage)
status: draft
created: 2026-10-03
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# CAESAR II → viewer

A CAESAR II pipe-stress model becomes a Pluto viewer model, `<base>.bin` + `<base>.features.json`, on the production machine: Windows PowerShell 5.1, C# 5 through `Config.ps1`, with no CAESAR II and no Python needed. This is branch `pipestress`.

- **Stage 1 (2026-10-03): geometry.** The whole pipe with its components, and one marker group per restraint combination.
- **Stage 2 (2026-10-04): results and supports.** The Excel output adds, per load case:
  - displacements, for contours and the deformed shape;
  - local element forces and code stresses on the pipe;
  - restraint loads at the supports.

  The sidecar carries one `supports` item per support node, which the viewer draws as 3D symbols.

Verified on the generic fixture and in the browser. The production-machine check (PS 5.1 compile, a real job) is still to do, so this note stays `draft`.

The other direction, a `.cii` built from a TOML config, is the [[vault/arms/pipe-builder|pipe-builder]]. Its `-Preview` runs this arm on the file it writes.

## Run

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Run-Caesar.ps1 `
  -Neutral "<project dir>\job.cii" -Results "<project dir>\job.xlsx" -Out "<project dir>\viewer\job"
```

- **`-Results`**: CAESAR's output written to Excel (Output Processor → Microsoft Excel), one report per tab.
  - Reports used: displacements, restraint summary, local element forces, code stresses. Other tabs are listed as skipped.
  - An `.xlsx` or `.csv` is read directly, also while it is open in Excel.
  - An `.xls` (Excel 97-2003) or `.xlsb` goes through Excel first. It is opened read-only with macros, events and prompts off, saved as a temporary `.xlsx` under `%TEMP%`, and deleted after the export. This needs Excel on the machine; otherwise save the workbook as `.xlsx` yourself.
  - Without `-Results`, or when no report tab is recognised, the export is geometry only.
- **`-Out <base>`** writes `<base>.bin` and `<base>.features.json`.
  - Default: beside the `.cii`, with spaces turned to underscores.
  - An existing folder, or any path ending in `\` or `/`, means `<folder>\<name>`.
  - **Output inside the Pluto repo is refused.** Project models never land here.
- **`-ModelId`**: default `caesar/<name>`.
- **`-ArcStepDeg`**: bend chord step, default 7.5°, kept within 0.5 to 45°.
- **Output:** progress lines start with `[step]`; the last line is `RESULT {json}` (paths, load cases, counts, warnings). The export refuses a neutral file it could not read cleanly (a VERSION line that is not a number, or an element count that differs from CONTROL) and writes nothing.
- **Open it:** pick the `.bin` and the `.features.json` together, or use `viewer/index.html?bin=…&features=…`. The groups and the support symbols live in the sidecar. CAESAR models are Y-up, and the file says so (META `upAxis`), so the viewer opens them Y-up without changing your Z-up setting.

From C#: `CaesarToPluto.Export(cii, resultsOrNull, outBase, modelId[, CaesarOptions])` returns a `CaesarExportResult` with `Summary()`, `ToJson()` and `Warnings`.

## In the viewer

- **Beams panel:** the pipe's components, in kind runs.
  - Displacements: `DX DY DZ |D|` in the model length unit, `RX RY RZ` in degrees.
  - Forces: `Axial fx`, `Shear fy`, `Shear fz`, `Shear |V|`.
  - Moments: `Torsion mx`, `Bending my`, `Bending mz`, `Bending |M|`.
  - Stresses: `SLP`, `F/A`, `Bending stress`, `Torsion stress`.
  - Code: `Code stress`, `Allowable`, `Code ratio`. A load case without a code check (B31.1 OPE) says "no beam results in this LC".
  - SIFs: in-plane, out-plane, torsion, axial.
- **Deformation:** the deformed shape works at true scale ×1 or auto-scaled. The support symbols stay where the supports are, and a line runs to each displaced node.
- **Support symbols** follow CAESAR II:
  - anchor: a square plate through the pipe;
  - every other restraint: slim arrows pointing at the pipe along its line of action;
    - two arrows for both directions, one for a one-way;
    - a rest (`+Y`) or a hanger: an arrow below the pipe;
    - a guide: arrows across the pipe;
    - a limit stop: arrows along the top of the pipe;
    - a rotation: a double-headed arrow.
- **Supports panel:** Show, Size, Colour.
  - Colour is the support group colour, the restraint kind, or a restraint load (`Restraint FX … |M|`) in the current load case.
  - Hover a symbol for its restraints and `|F|` / `|M|`. Click to pin a card with the loads on the restraint in every load case, and the node displacement.
  - Unticking a support combination in Groups › Nodes hides its symbols.
- **The restraint loads** do not appear in the Beams component list: on the pipe they would sit only on the beam ends at supports.

## What the files become

Code: `scripts/arms/CaesarNeutralReader.cs` → `CaesarGeometry.cs` → [`CaesarResults.cs`] → `CaesarToPluto.cs`.

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
> - **Synthetic nodes** (near points, arc vertices, valve mids) have ids from 10,000,000. Each records its two bracketing real nodes, which the results use for interpolation. Use `IsSynthetic`, not the id range.
> - **Checked on the fixture** against CAESAR's own COORDINATE REPORT in the input echo:
>   - all 128 rows within 0.0016 in;
>   - the report prints the tangent intersection point for a bend's TO node.
>
>   The far points were checked against CAESAR's displacements of the neighbouring nodes. Every arc vertex lies at radius R (1.5e-13 relative).

> [!info]- Reading the Excel output (CaesarResults)
> - **Workbook** (`CaesarWorkbook`), by magic bytes, not extension:
>   - **An `.xlsx`** is read as a zip of XML, opened with `FileShare.ReadWrite`, so a workbook open in Excel still reads. The reader handles:
>     - the officeDocument part from `_rels/.rels`, with relative or absolute targets;
>     - shared strings (rich-text runs joined, phonetic runs skipped) and inline strings;
>     - every cell at its true row and column;
>     - strict and transitional files alike (elements are matched by local name).
>   - **A text file** reads as CSV. The separator (`,` `;` or tab) is chosen by count, with a decimal-comma fallback.
>   - **An `.xls` or `.xlsb`** is refused with a message; the runner converts both first.
> - **Reports.** Each tab is one report: a title row, the load case label (`CASE 1 (OPE) W+T1+P1`), then a header row starting `Node`, with each header carrying its unit (`DX in.`, `fx lb.`, `Code lb./sq.in.`). The title gives the kind; without one, the headers do.
>   - **DISPLACEMENTS:** one row per node.
>   - **RESTRAINT SUMMARY:** node-major. A type row, then one row per case (`  1(OPE)`), then a MAX row, which is skipped.
>   - **RESTRAINTS REPORT:** the single-case form, summed per node.
>   - **LOCAL ELEMENT FORCES** and **`<code>` STRESSES REPORT:** FROM / TO row pairs per segment, with bends split at their nodes.
>   - **GLOBAL ELEMENT FORCES** and **CODE COMPLIANCE** are recognised and not used; the per-case stress tabs carry the same values.
> - **Load cases** are keyed on CAESAR's case number. The name keeps the type and the definition: `L1 (OPE) W+T1+P1`, `L4 (EXP) L4=L1-L3`.
> - A row or case that cannot be read gives a warning, never an exception.

> [!info]- Results on the model (CaesarToPluto, stage 2)
> - **Displacements** are per node, fanned to the beam ends, and converted to the model's length unit.
>   - Bend points and valve mids are interpolated between their two real nodes.
>   - A real node missing from the report is filled from its neighbours along the run, and counted.
>   - A load case covering under 95 % of the pipe's real nodes is dropped, with a warning: the viewer would draw a missing displacement as zero, a fake kink.
>   - `displacementVector` = DX DY DZ.
> - **Element forces** are written as internal forces: end A = −(FROM row), end B = +(TO row).
>   - A value runs on continuously from one element to the next. CAESAR reports the force *on* each element end.
>   - `|V|` and `|M|` do not depend on the local axes.
>   - Each segment is matched to the pipe path between its two real nodes and interpolated by path length onto the chords and valve halves it covers.
>   - Tee surface nodes stand for their host node.
> - **Stresses** are as reported, except torsion, which is signed like `mx`. Blank cells (rigid elements, cases without a code check) stay NaN.
> - **Restraint loads** (kind `restraint`) come from the restraint summary.
>   - They are the loads *on* the restraint, in CAESAR's sign, summed per node.
>   - They are written at every beam end touching the node, and NaN elsewhere.
>   - Kind `restraint` is in [[vault/format/v4-schema|v4-schema]] §4.1.
> - **Profile:** with results, `Write(true)` and the appends; without, `Write(false)`. The `geometryHash` is the same either way, so a sidecar binds to both.

> [!info]- Supports in the sidecar
> - **One item per support node**, built from the `.cii` alone.
>   - Named `<node> <combination>` (`2110 +Y`), with tags `caesar`, `support`.
>   - `dof` letters (`tx` … `rz`: `fixed`, `+` / `-`, `gap`, `spring`, `guide`, `limit`, `hanger`, `imposed`).
>   - `source` `{exporter, node}`.
>   - `restraints[]`: one entry per restraint (spec: [[vault/format/features-sidecar|features-sidecar]] › supports).
> - **Restraint kinds:**
>   - `ANC` → `anchor`: all six dof fixed.
>   - `X` `Y` `Z` → `translation`, along the cosines or the axis.
>   - `+X` … `-Z` → `oneway`, `direction` the way it pushes.
>   - `RX` `RY` `RZ` → `rotation`.
>   - `GUI` → `guide`, `direction` = up × pipe axis. On a pipe within 5° of vertical it is `vertical: true` (both horizontal dof).
>   - `LIM` → `limit`, along the pipe axis.
>   - MISCEL_1 hangers → `hanger` (direction up, cold / hot load).
>   - DISPLMNT → `imposed`.
>   - Anything else → `other`.
> - **CAESAR's guide and limit cosines are dummies**, so both come from the pipe axis at the node.
> - `gap`, `friction`, `stiffness` (non-rigid only), `cnode` and `tag` are written when present.
> - **Re-export:** a support item keeps its `id` and `hidden` flag by name, and hand-made items are kept (`FeaturesSidecar.WriteSupports`).

> [!info]- The viewer model (CaesarToPluto)
> - **Binary:**
>   - Beams only. Node ids are CAESAR node numbers.
>   - Beam id = element ordinal × 100 + piece. LABL = `FROM-TO` plus the element name, `arc k/n` or `valve a/b`.
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
>   5. **one node group per restraint combination at a node**, e.g. `+Y + GUI`, `ANC`, `Y w/gap + Z w/gap`. They colour and show / hide the support symbols, and draw as markers.
>
>   After the groups comes the `supports` section.

## Fixture result

The generic job in `scripts/caesar/testdata/` (README there, with the scrub checklist):
- **Geometry:**
  - 245 beams (95 pipe, 132 bend chords, 3 reducer, 4 valve halves, 4 flange, 5 flange pair, 2 expansion joint);
  - 6 tapered;
  - 25 groups, including 13 support combinations at 45 nodes.
- **Results:**
  - 6 load cases (2 OPE, 1 SUS, 3 EXP combinations) and 34 components;
  - displacements in all 6 cases, with no node filled or missing;
  - 138 force / stress segments placed, 0 unmapped;
  - restraint loads at all 45 support nodes.
- **Supports:** 45 items with 112 restraints: anchor 4, translation 41, rotation 36, one-way 22, limit 5, guide 4.
- **Checked against the workbook:**
  - displacements at sample nodes in all cases;
  - internal forces, code stress, allowable, F/A and torsion on a sample beam;
  - restraint loads (the pin card matches the Restraint Summary row for row);
  - `|M|` continuous across 1,164 node / case pairs (worst 0.010 %).

## Checks

- **Readback:** `node viewer/tests/readback_caesar.js <base> [--expect <RESULT json>]`. It exits 1 on failure. It checks:
  - structure, tapers against reducers and valves, `upAxis`, units, the hash binding and groups;
  - with results, the load case names and component count against the RESULT;
  - displacements finite at every beam end, one value per node, and `|D|` = hypot;
  - restraint loads only at support nodes, and the same on every end there;
  - `|V|` / `|M|` = hypot of their parts, and forces on ≥ 95 % of beam ends;
  - the `supports` items.
- **Symbols:** `node viewer/tests/test_supports.js`.
- **On the production machine, before the first real job:** dot-source `scripts\lib\Config.ps1` in a Windows PowerShell 5.1 `-NonInteractive` session (the compile check). Then run the runner on the fixture (`-Results` with the fixture workbook), with the output outside the repo, and open it. The `.xls` / `.xlsb` path through Excel has only had its no-Excel error path tested so far.

## Next (stage 3)

- mitred ring joins at bends (removes the chord seams);
- nozzles;
- coupling recognition by name;
- beam envelopes (max over load cases) for the code ratio and the restraint loads;
- per-restraint loads (the Restraints report) when a node has several restraints.
