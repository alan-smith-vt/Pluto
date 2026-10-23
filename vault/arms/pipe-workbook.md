---
title: Pipe model workbook (Excel ↔ the pipe model)
status: draft
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Pipe model workbook

A pipe-stress model as an Excel workbook you edit, built back into the model by `scripts/caesar/Build-Pipe.ps1`. That gives a CAESAR II `.cii`, a viewer preview, or both. The workbook is the model's master copy, and the viewer only shows it.
- The workbook is written from:
  - a `.cii` job;
  - a TOML config ([[vault/arms/pipe-builder|pipe-builder]]);
  - a model JSON;
  - a small template (`-NewWorkbook`).
- **An unedited workbook gives back its source exactly.** For a CAESAR job, that is the `.cii` byte for byte: what the sheets do not show is carried as written ([[vault/format/pipe-model|pipe-model]]).
- **Requirements:** Windows PowerShell 5.1 and C# 5 through `Config.ps1`. Excel is needed only to edit the workbook; nothing here opens Excel.

The workbook is also the form you edit the model in:
- **Guidance:** coloured headers by kind of column, a note on every header, a hint on every input cell, and dropdowns.
- **Highlighting:** red where the build would stop, amber where a look is worth it.
- **A Start page** with live checks.
- **A 3D View:** a chart of the model that follows every edit.

Code:
- `scripts/arms/PipeWorkbook.cs`: the sheets, both ways.
- `scripts/arms/PipeWorkbookGui.cs`: the form: column kinds, hints, rules, the Start page, the 3D View and its formula sheets.
- `scripts/lib/writers/XlsxWriter.cs` (`XlsxBook`): a plain OOXML writer:
  - styles, formulas, conditional formatting, data validations with hints, notes, links, merged cells, auto-filters and XY charts;
  - no document properties and a fixed entry time, so the same model gives the same bytes.
- The reader is the CAESAR arm's `CaesarWorkbook`. It reports error values and formulas with no saved value by their address.

**Status: verified on Linux (2026-10-22), still `draft`.** Still to do on the production machine:
- the PS 5.1 compile;
- opening a written workbook in Excel and saving it, with no repair prompt;
- a real job round-tripped.

## The loop

```powershell
# a CAESAR job -> a workbook (or: -In line.toml -Xlsx, or -NewWorkbook -Out <project dir>\new_line)
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.cii" -Xlsx

# edit job.xlsx in Excel; keep a preview current while you do (Ctrl+C stops)
powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.xlsx" -Out "<project dir>\job_v2" -Preview -Watch

# when the view looks right
powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.xlsx" -Out "<project dir>\job_v2" -Cii
```

1. **Open the preview in the viewer** with **Choose files…** (Chrome or Edge), picking `job_v2.bin` and `job_v2.features.json` together. Tick **Auto-reload**: every save in Excel rebuilds the preview, and the viewer reads it again with the camera and the load case kept.
2. **`-Cii` says where the model stands** (`[review ]`): it is the one you previewed, it changed since the preview, or no preview was made.
3. **Every read lists what the workbook changed** against the model it was written from (a compare by meaning): elements, nodes moved, restraints, SIFs, loads.

> [!info]- The sheets
> | Sheet | One row per | Columns |
> |---|---|---|
> | Settings | setting | title, notes, vertical axis (Y / Z), ambient; read-only: units, source, base fingerprint |
> | Elements | element, in CAESAR's order | Id, From, To, DX DY DZ, Type, Section, Process, bend radius / nodes / type, rigid weight, reducer OD2 / wall2, expansion joint (axial, transverse, bending, torsion, effective ID), name, line; then a collapsed group (+) of one-element overrides: OD, wall, insulation, corrosion, densities, material, E, Poisson, T1–T9, P1–P9, fluid density, bend miter points, fitting thickness, K |
> | Sections | pipe size | name, OD, wall, insulation, corrosion, insulation density, material, E / Poisson / pipe density (blank = CAESAR's database) |
> | Processes | operating condition | name, T1–T9, P1–P9, fluid density |
> | Restraints | restraint | Id, node, type (CAESAR's), CNODE, stiffness (blank = rigid), gap, friction, cosines (blank = the type's own axis), tag |
> | SIFs | SIF / tee | Id, node, type (3 = welding tee, 0 = user SIFs), SIF in / out, surface node |
> | Forces / Displacements | node and load vector | Id, node, vector (1–9), FX…MZ / DX…RZ. A blank displacement DOF is free; 0 holds it |
> | Uniform / Wind | element, or a range of elements with the same values | Element (`E4`, a range `E3:E9`, or blank with From and To), vector, UX UY UZ, in g's / type, shape factor, wave data |
> | Joints | joint | Id, node, CNODE, kind, frame (pipe / global), DOFs (`axial gap=0.125 k=5000; shear; torsion; bending gap=2`), SIF, tag |
> | Coords | COORDS entry | node, X Y Z |
> | Start | — | the landing page: how to use the workbook, links to the sheets with their row counts, the live checks, the colour key, a reference |
> | 3D View | — | the model drawn live; its controls (view, highlight, labels, restraints), figures, key |
> | Nodes, Carried | — | read-only: rebuilt on every export, never read |
> | Lists, PlotCalc, PlotPts (hidden); `_pluto` (very hidden) | — | dropdown sources; the 3D View's formulas (one row per element) and the chart's points; the base model as exact JSON and its fingerprint |
>
> - **Units are the job's.** Each header says which (`DX (in)`).
> - **Sections and Processes** are named from what they hold (`8in STD ins2`, `T300 P150`).

> [!info]- Reading rules
> - **Ids:**
>   - A row with an Id is that item of the base model.
>   - A row without one is a new item.
>   - A base item with no row is deleted, and the report says so.
>   - A copied row (a repeated Id) is read as a new item, with a warning.
> - **Row order** on Elements is CAESAR's element order.
> - **Unchanged values stay exact.** A value equal to the base's (to 1e-9) keeps the base's exact value. That is why an unedited workbook gives back its source, even after Excel saves numbers its own way.
> - **Section / Process:** a blank cell takes the row above's. An override cell wins over the section's or process's value for that one element.
> - **Bend radius:** a number, or a name: `LR` or `Long` (1.5 × nominal), `SR` or `Short` (1 ×), `3D`, `5D`. The nominal size comes from the element's OD, inch or metric.
>   - **CAESAR's coded radii** (a negative multiple of −1.0101) show as their names: −3.0303 is `3D`, −4.0404 `Short`, −5.0505 `5D` (and −1.0101 `Long`, which CAESAR itself writes as a plain number).
>     - A name read back over CAESAR's code for that name keeps the code, so CAESAR gets its own file back. Any other name becomes the plain radius.
>     - The codes were confirmed with CAESAR 15.01: [[vault/format/caesar-neutral-loads|caesar-neutral-loads]].
>   - **Codes CAESAR reads as no radius** (−2.0202, −6.0606 … −9.0909) stay numbers, are carried as written, and show amber. Do not make a code positive: +4.0404 is a literal 4.04 radius, a different bend.
> - **Uniform / Wind are per element.** An element no row names has none: CAESAR's file does not carry them forward.
>   - **Element:**
>     - an Id;
>     - a range in the Elements sheet's order (`E3:E9`), including new rows between;
>     - or blank, with From and To: one element, or the chain of consecutive elements from the one starting at From to the one ending at To.
>   - **Export:** consecutive elements with the same values, each starting where the last ended, share one row.
>   - **Refused:** the same vector given twice on an element, a range given backwards, and a From / To that no chain joins.
> - **Bend nodes:** `angle:node` pairs, where `M` is CAESAR's mid point (`M:19; 0:18`).
> - **A blank bend fitting thickness** follows the wall.
> - **Headers:** a length column's header may say another unit (`DX (ft)`; in, ft, mm, cm, m) and its values are converted. Any other unit change is an error. Columns are found by name, so they may be reordered.
> - **What is refused, by address** (`Elements!D6`):
>   - an error value;
>   - a formula Excel never calculated (save the workbook in Excel first);
>   - text where a number belongs.
>
>   All problems are collected and reported together.
> - **The whole result is checked as CAESAR will see it**, slot limits included, before anything is written.
> - **A missing `_pluto` sheet:** every row is new and nothing CAESAR wrote is carried. The model still builds, from the sheets alone.

> [!info]- Runner switches (Build-Pipe.ps1)
> - **`-In x.xlsx`:** the workbook. The report lists its edits.
> - **`-Xlsx`:** writes `<base>.xlsx` from any input. It never writes over the input.
> - **`-NewWorkbook -Out <base>`:** writes the template (two legs, a bend, two anchors and a rest).
>   - With `-Seed <job.cii>`, it takes the job's settings (English units only).
>   - An existing file needs `-Force`.
> - **`-Watch`:** rebuilds the chosen outputs (default `-Preview`) on every save. A save that does not build is reported, and the last good outputs stay. The preview is written to temporary names and moved into place, with the sidecar's own groups merged in, so a viewer reloading never sees half a file.
> - **`-Cii` guards an existing `<base>.cii`.** It replaces the file only if one of these holds:
>   - this runner wrote it;
>   - it is the model's own source;
>   - nothing in it would change.
>
>   Otherwise it was changed outside (CAESAR, a person): the run stops, shows the differences, and needs `-Force`.
> - **`<base>.build.json`** records what was written from what: the `.cii`'s bytes, the model and the input fingerprints, and the preview's model. It is how the runner knows its own `.cii` and the previewed model.

> [!info]- Viewer: Reload and Auto-reload
> - **Reload** reads the model and its sidecar again from where they came from:
>   - the picked files' handles (**Choose files…** in Chrome or Edge);
>   - or the page's `?bin=` / `&features=` URLs.
>
>   The camera and the load case stay.
> - **Auto-reload** checks every 2 s, and reloads once a change has held still for one check.
> - **Unsaved features:** Auto-reload waits while the features have unsaved edits, and says why; a manual Reload asks first.
> - **The plain file input** (browsers without the File System Access API) gives snapshots that cannot be read again: Reload says so.

> [!info]- The workbook as a form
> - **Headers** are coloured by kind of column; the Start sheet's colour key explains them.
>   - Kinds: Ids and nodes, geometry, properties, bends, components, loads, names, overrides, read-only.
>   - Each header carries a **note** (hover for the red corner) explaining the column.
> - **Every input column has a hint and a rule**: the hint shows when a cell is selected, and the rule gives a warning that can be overridden.
>   - **Number columns** take numbers. Positive-only columns, whole numbers, node numbers and cosines (−1..1) each have their own rule.
>   - **Dropdowns:**
>     - with a warning for a value not on the list: element type, bend type, restraint type, SIF type, load vector, In g's, wind type, joint frame, vertical axis;
>     - free entry allowed: Section, Process, joint kind;
>     - the bend radius takes a number, or `LR`, `SR`, `Long`, `Short`, `3D`, `5D`.
> - **Highlighting (conditional formatting):**
>   - **Red** = the build would refuse it:
>     - text or an error where a number belongs;
>     - a value not on its list, an unknown Section / Process, an unknown element Id or range (Uniform, Wind);
>     - a missing From / To, From = To, a missing section on the first row;
>     - a reducer without OD2 / Wall2, bend nodes without `angle:node`;
>     - a vector outside 1–9, the same node and vector twice;
>     - a duplicate section or process name, a cosine above 1.
>   - **Amber** = worth a look: a node no element has (bend nodes count), a copied Id, a zero-length element.
> - **Start's checks** count those cells live: error values; text where a number belongs (per sheet); rows without From / To; unknown names and types; unknown element Ids; vectors outside 1–9; nodes on no element; Ids used twice. Each shows OK, check (amber) or fix (red).
> - **Kept as before:** the input sheets keep their columns, values and order, so the reader is unchanged. Start, 3D View, Nodes, Carried and the hidden sheets are never read. Only the input sheets' cells are read, so the plot's formulas never trip the "formula with no saved value" check. The workbook asks Excel to calculate everything when it opens.

> [!info]- The 3D View
> - **Controls:**
>   - **View:** Iso (CAESAR's), Plan, Front, Side, or Custom with an azimuth and elevation.
>   - **Highlight element:** an Id, drawn thick and orange with a marker; Figures names its nodes, length and type.
>   - **Node numbers:** yes / no. Labels taken from cells need Excel 2013 or later; LibreOffice does not show them.
>   - **Restraints:** yes / no.
> - **Drawn:**
>   - the pipe;
>   - rigids, valves and flanges as thick dark lines;
>   - expansion joints dashed green;
>   - bends as curves with their radius (a name or a CAESAR code from the section's OD, through a nominal-size table);
>   - anchors as squares, vertical supports as triangles, other restraints as diamonds;
>   - an X / Y / Z triad.
>
>   Figures: elements drawn, rows not drawn, pipe length, extents.
> - **How it is computed** (PlotCalc, one row per Elements row; all plain Excel 2007 functions, no macros, no array formulas):
>   - **Columns** are found by header name, so the sheet follows reordered columns.
>   - **Positions** follow CAESAR's rules. A From node is where an element above reached it, else its Coords entry, else where the element above ended. A bend's element runs to the corner and the next element starts there, trimmed by the bend's tangent.
>   - **Backward branches:** a piece of the model that meets an already placed piece further down the sheet (a branch entered from its far end) is moved onto it, in two passes.
>   - **Projection:** azimuth, then elevation, with Z-up jobs turned first. The chart is square and a hidden square frame keeps both scales equal.
> - **Limits:**
>   - nodes on a bend's curve (M, angle nodes) are not drawn;
>   - a CAESAR code that is no radius is drawn as a corner, as CAESAR treats it;
>   - the formula rows end 300 elements past the job's count, so a much longer model needs the workbook written again (`-Xlsx`);
>   - where a loop of the model does not close, the place it is closed may differ from Pluto's viewer by that gap;
>   - the workbook is about 0.4–0.8 MB (the formulas).
> - **Two LibreOffice quirks, worked around:**
>   - It drops line segments when a chart has a manual plot-area layout, so the chart keeps its automatic layout and the key is in cells.
>   - It merges points that nearly coincide even across the empty row between elements, and then drops that element's first segment. So each element is drawn from its end back to its start, so its first point is not where the element before it ended, and that point is also moved by 0.1 % of the frame (under the line's width).

## Verification (2026-10-22, Linux)

- **Round trips:** workbook → model is exact (the same model fingerprint), and the `.cii` is unchanged, for:
  - the 15.01 fixture;
  - a synthetic metric job with carried blocks;
  - three 11.00 jobs;
  - the load model;
  - the three TOML configs;
  - the template.

  Imported jobs come back byte for byte.
- **Edits made like a user's** (openpyxl, which re-saves the file as a spreadsheet program does: shared strings, its own styles):

  | Edit | Result |
  |---|---|
  | re-saved with no edits | exact |
  | one DX | 1 element, 1 line of the `.cii` |
  | a section's wall | its 32 elements |
  | a deleted row | that element, reported |
  | added restraint and force rows | added |
  | `+Y` to `Y` | the type changes |
  | DX in feet in the header | converted, unchanged |
  | a formula with no saved value, an error value | refused with the address |
  | a blank Section | the row above's |
  | an OD override | one element |
  | a copied row | new, with a warning |
  | no `_pluto` sheet | the same geometry |
  | uniform, wind and displacement edits | applied |
  | a uniform range extended by one element (a CAESAR-written job) | that element gains it, one new block |
  | wind by From and To over the whole chain | on every element; only those that had none change |
  | a range split, U1 changed on its second part | 3 elements, 3 lines of the `.cii` |
  | a vector twice, a range backwards, a From / To no chain joins | refused, with the row |
  | a new row on the template | builds |
  | `SR` | the radius |
- **Runner under pwsh:**
  - `.cii` → workbook → `.cii` byte-identical;
  - preview, then `[review ]` names the previewed model; after an edit it says the model changed;
  - the runner's own `.cii` is replaced, a `.cii` changed outside is refused with the difference, `-Force` replaces it;
  - `-NewWorkbook`, then preview and `.cii`;
  - argument errors.
- **`-Watch`:** an edit rebuilds; a broken save keeps the last good preview; the original restores it; no temporary files are left.
- **Viewer:**
  - `test_reload.js`: the reload logic headless;
  - Chromium: a rebuilt model (17 → 18 beams) picked up by Auto-reload in about 4 s, the camera unmoved, no errors;
  - all other viewer tests.
- **Not checked here:** Excel itself (no Excel on Linux; LibreOffice here could not load files). openpyxl reads the structure as intended: sheet states, frozen panes, lists and the collapsed group.

## Verification of the form (2026-10-23, Linux)

- **Schema:** 11 workbooks are valid against Excel's schemas (Microsoft's Open XML SDK validator, Office 2013): the samples, the fixture, a job exported by CAESAR 15.01, the TOML configs, the template.
- **The 3D View's formulas, recalculated by LibreOffice**, against Pluto's geometry on the same 11 models:
  - every element end and every bend's near and far point agree to 10⁻⁴ (fixture: 125 element ends, 13 bends, within 10⁻¹¹), except for:
    - the `.cii`'s 7-digit COORDS (≤ 6×10⁻⁴), where the workbook holds the exact value;
    - one public sample whose loop misses by 8.4 mm, closed 6 mm differently.
  - Z-up, metric, a backward branch, and branches from Coords are all covered.
- **Rendered by LibreOffice** (PDF / PNG):
  - Start's links, counts and checks;
  - headers and highlighting with deliberate errors (text in DX, an unknown type and section, From = To, a radius of −3, a row without nodes, a copied Id, an unknown restraint type, a cosine of 1.5, a node on no element, a vector of 12, an unknown element Id, 'maybe' in In g's);
  - the 3D View in Iso and Plan, with a highlighted element.
- **Round trips and edits as before:** every workbook reads back to its exact model and its source `.cii` byte for byte; the openpyxl edit tests and `-Watch` pass.
- **Not checked here:** Excel itself. On the production machine:
  - open a written workbook (no repair prompt);
  - the 3D View draws;
  - node numbers show;
  - recalculation is quick on a real job.

## Not yet

- **A merge mode:** applying a workbook's edits onto a job CAESAR has changed since. Today the guard stops, and the way on is a new workbook from the changed job.
- **Provenance in the viewer's status line** (Stage D, with the loads drawn).
