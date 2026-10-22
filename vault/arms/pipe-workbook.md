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

Code:
- `scripts/arms/PipeWorkbook.cs`: the sheets, both ways.
- `scripts/lib/writers/XlsxWriter.cs` (`XlsxBook`): a plain OOXML writer, with no document properties and a fixed entry time, so the same model gives the same bytes.
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
> | Uniform / Wind | entry, on the element where entered (CAESAR carries it forward) | element Id (or From and To), vector, UX UY UZ, in g's / type, shape factor, wave data |
> | Joints | joint | Id, node, CNODE, kind, frame (pipe / global), DOFs (`axial gap=0.125 k=5000; shear; torsion; bending gap=2`), SIF, tag |
> | Coords | COORDS entry | node, X Y Z |
> | Nodes, Carried, Help | — | read-only: rebuilt on every export, never read |
> | Lists (hidden), `_pluto` (very hidden) | — | dropdown sources; the base model as exact JSON and its fingerprint |
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
> - **Bend radius:** a number, or `LR` / `SR` (1.5 / 1 × nominal, for a standard size in inches).
>   - **Coded radii:** CAESAR may write a radius as a negative coded value, a multiple of −1.0101 such as −4.0404. It is the same kind of code as −2.0202 for "M" in the angle fields; most likely a radius chosen by name in CAESAR's input.
>   - Such a value is read and written as it is, so CAESAR resolves it as before. Do not make it positive: +4.0404 is a literal 4.04 in radius, a different bend.
>   - Until the codes are confirmed, the viewer and compare's node positions treat these bends as sharp corners. The import and the reader say so (first seen 2026-10-22 on a real job; the public samples have none).
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

## Not yet

- **A merge mode:** applying a workbook's edits onto a job CAESAR has changed since. Today the guard stops, and the way on is a new workbook from the changed job.
- **Provenance in the viewer's status line** (Stage D, with the loads drawn).
