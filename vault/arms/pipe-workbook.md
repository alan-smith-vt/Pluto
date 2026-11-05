---
title: Pipe model workbook (Excel ↔ the pipe model)
status: draft
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Pipe model workbook

A pipe-stress model as an Excel workbook you edit, built back into the model by `scripts/caesar/Build-Pipe.ps1`. Its machinery (form, Start sheet, launchers, base, reading, refresh) is the shared [[vault/arms/workbook-kit|workbook-kit]]. That gives a CAESAR II `.cii`, a viewer preview, or both. The workbook is the model's master copy, and the viewer only shows it.
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
   - **Check what you are about to export** (Stage D): the **Loads** panel draws every force, moment, imposed displacement, uniform load and wind by vector; the **Checks** tab (✓) lists what the build found (an unsupported piece, a branch without a tee, a bend with no room, a load off the model, layouts to check in CAESAR) and flies to each; hovering a pipe shows its size, T1, P1, material; **Node numbers** labels the nodes; the Model panel names the workbook and when it was saved.
2. **`-Cii` says where the model stands** (`[review ]`): it is the one you previewed, it changed since the preview, or no preview was made.
3. **Every read lists what the workbook changed** against the model it was written from (a compare by meaning): elements, nodes moved, restraints, SIFs, loads.

> [!info]- The sheets
> | Sheet | One row per | Columns |
> |---|---|---|
> | Settings | setting | title, notes, vertical axis (Y / Z), ambient; read-only: units, source, base fingerprint, node numbers (live and its step, or fixed and why) |
> | Elements | element, in CAESAR's order | Id, From, To, DX DY DZ, Type, Section, Process, bend radius / nodes / type, rigid weight, reducer OD2 / wall2, expansion joint (axial, transverse, bending, torsion, effective ID), name, line; then a collapsed group (+) of one-element overrides: OD, wall, insulation, corrosion, densities, material, E, Poisson, T1–T9, P1–P9, fluid density, bend miter points, fitting thickness, K, hydro pressure, mill tolerance − / +, seam weld |
> | Sections | pipe size | name, OD, wall, insulation, corrosion, insulation density, material, E / Poisson / pipe density (blank = CAESAR's database), mill tolerance − / + (% of the wall), seam weld (yes / no) |
> | Processes | operating condition | name, T1–T9, P1–P9, fluid density, hydro pressure (CAESAR's hydrotest case) |
> | Restraints | restraint | Id, node, type (CAESAR's), CNODE, stiffness (blank = rigid), gap, friction, cosines (blank = the type's own axis), tag |
> | SIFs | SIF / tee | Id, node, type (a dropdown of CAESAR's list, "3 - welding tee"; a bare number from an older workbook reads the same), SIF in / out, surface node, pad (a reinforced tee; amber without), crotch radius, weld mismatch |
> | Forces / Displacements | node and load vector | Id, node, vector (1–9), FX…MZ / DX…RZ. A blank displacement DOF is free; 0 holds it |
> | Uniform / Wind | element, or a range of elements with the same values | Element (`E4`, a range `E3:E9`, or blank with From and To), vector, UX UY UZ, in g's / type, shape factor, wave data |
> | Joints | joint | Id, node, CNODE, kind, frame (pipe / global), DOFs (`axial gap=0.125 k=5000; shear; torsion; bending gap=2`), SIF, tag. Couplings in a `.cii` or a TOML build come here (below); their CNODE restraints and SIF are off Restraints and SIFs |
> | Hangers | spring hanger (one per node) | Id, node, CNODE; Rate with Cold load (a spring as given) or Constant (a constant-effort support), both blank = CAESAR designs it; table, count, load variation, short range, max travel, space, rigid criteria, operating load, hardware weight, multi-load; anchors freed while it is designed (node + Y / XY / ZY / XYZ / all, two); tag. A workbook made before this sheet keeps its base's hangers |
> | Nozzles | flexible nozzle | Id, node, vessel node (blank = a rigid vessel), type (WRC 297, API 650, PD 5500, Custom), then the values: nozzle OD / wall, vessel type, vessel OD / wall, pad, reinforcing, distances to stiffeners, nozzle and fluid heights, vessel centreline X Y Z, vessel temperature and material, displacement vector, fluid SG, tank alpha / ΔT / E, the four custom stiffnesses. A column the row's type does not use is red |
> | Coords | COORDS entry | node, X Y Z |
> | Start | — | the landing page: how to use the workbook, links to the sheets with their row counts, the live checks, the colour key, a reference |
> | 3D View | — | the model drawn live; its controls (view, highlight, labels, restraints), figures, key |
> | Nodes, Carried | — | read-only: rebuilt on every export, never read |
> | Lists, PlotCalc, PlotPts, CheckCalc (hidden); `_pluto` (very hidden) | — | dropdown sources; the 3D View's formulas (one row per element) and the chart's points; the Start checks that look a node up, row by row; the base model as exact JSON and its fingerprint |
>
> - **Units are the job's.** Each header says which (`DX (in)`).
> - **Sections and Processes** are named from what they hold (`8in STD ins2`, `T300 P150`).

> [!info]- Ids (2026-11-05)
> The Id column is each row's link to its object in the model, not a row counter.
> - **A new row shows `new`** by formula as soon as its next column is filled. Nothing is typed, and `new` reads as a
>   new object.
> - **A row-number formula was asked for and declined:**
>   - inserting or deleting a row would shift every Id below it;
>   - on named objects that reads as a chain of renames, which re-point everything that names them;
>   - on CAESAR's Elements, the blocks carried with each element would move to the wrong one.

> [!info]- Couplings on the Joints sheet (2026-11-05)
> CAESAR keeps a coupling as CNODE restraints between two nodes at one point, one per DOF, and so does the TOML
> builder. Until 2026-11-05 a workbook made from a `.cii` or a TOML showed them as plain rows on Restraints and left
> Joints empty. `PipeJoints.Recognize` now runs after every `.cii` import and TOML build. It turns each such group into
> a joint: kind (coupling, ball, slip, hinge, custom), frame, DOFs with their gaps and stiffnesses, the node's user SIF,
> and the tag.
> - **A group is a joint when:**
>   - its two nodes are on elements and at one point;
>   - it has three or more single-DOF restraints (X Y Z RX RY RZ, skewed or not), no friction, no two along one
>     direction, and one tag;
>   - they fit the pipe frame (axial, shear pair, torsion, bending pair) or the global one.
>
>   A strut or a one- or two-DOF tie stays on Restraints.
> - **The restraints stay in the model where they were**, marked with the joint (`PipeModelRestraint.Joint`), so the
>   `.cii` is written exactly as before, and the viewer and the review see the same restraints.
> - **Editing a joint edits them** (`PipeJoints.Sync`):
>   - a value changes in place;
>   - a DOF added becomes new restraints after the joint's others;
>   - a DOF removed takes its own away;
>   - another frame replaces them all;
>   - a row deleted removes them and the SIF;
>   - a new row adds its restraints to the model.
> - **Checked:**
>   - every test model and example writes the same `.cii` before and after (16 models; couplings found in 6: e4's 4,
>     the sample CAESAR model's 9, one each in e11, the probe, the example and the patterns);
>   - workbook to `.cii` is the same bytes;
>   - the viewer's data is unchanged;
>   - edits to a gap, a stiffness, a SIF, a frame, a deleted and an added joint each change only their own lines.
> - **A workbook made before this** keeps its couplings on Restraints (Refresh does not move them). Make it again
>   from the `.cii` to get them on Joints.

> [!info]- Node numbers (live)
> - **From and To are formulas** (2026-10-29), so inserting a node renumbers the nodes after it. Settings says `live, step 10` (the commonest increment) or `fixed` and why.
>   - Every number a row holds is `step × ROW() + constant` (`=10*ROW()`). A row's From is `IF(ROW()=ROW($A$<the run's first row>), <the run's start>, INDEX($C:$C,ROW()-1))`: the To of the row above. A run is a stretch of rows each starting where the row above ends; a branch starts on the cell that holds its node (`$C$3`), and an element closing a loop ends on one.
>   - The other sheets name a node by the Elements cell that holds it (`=Elements!$C$11`), and a bend's own nodes by its To (`"M:"&(C12-1)`), so inserting or deleting rows carries them along.
>   - To never reads From, so Excel sees no circular reference. Every formula is written with its value.
> - **To add a node inside an element:** insert a row above it, copy the row above into the new row (select both, Ctrl+D), then give the new row the first part of the element's DX / DY / DZ and the element the rest. Every node below moves up one step; supports, loads, SIFs and branches stay where they are.
>   - **The first element of a run** (the row above belongs to another run): insert the row below it instead and give the new row the second part. Anything that sat at the element's old end node (a support, a branch) now names the new node in between: point it at the new row's To.
>   - **Deleting a row** closes the gap: the next element starts where the one above ended, and the numbers below move down one step. Anything that named the deleted row's To shows `#REF!` (Start counts it): give it its node.
> - **When the numbers stay fixed:** live numbering needs the numbers the rows hold to rise down the sheet (or all fall); otherwise moving the rows below an inserted row could land on a number a row above holds (an imported job numbered out of order). Type numbers as before, or renumber the job so they rise down the sheet.
> - **Saved without values** (by a program that does not calculate, such as a script library): Pluto works out its own node formulas and plain arithmetic itself; any other formula still has to be saved in Excel first.

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
> - **Uniform / Wind name the elements they act on.** An element no row names has none. CAESAR carries a load forward to the elements after it, so Pluto writes a block where a load starts or changes and ends it where it stops ([[vault/decisions/2026-10-24-uniform-wind-carry-forward|carry forward]]).
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
>   - a formula saved without its value that Pluto cannot work out itself (it works out its own node formulas and plain arithmetic; for anything else, save the workbook in Excel first);
>   - text where a number belongs.
>
>   All problems are collected and reported together.
> - **The whole result is checked as CAESAR will see it**, slot limits included, before anything is written.
> - **A missing `_pluto` sheet:** every row is new and nothing CAESAR wrote is carried. The model still builds, from the sheets alone.

> [!info]- Runner switches (Build-Pipe.ps1)
> - **`-In x.xlsx`:** the workbook. The report lists its edits.
> - **`-Xlsx`:** writes `<base>.xlsx` from any input. It never writes over the input.
> - **`-In x.xlsx -Refresh`** (2026-11-01): brings a workbook to the layout of the Pluto that runs it, over itself, so an older workbook needs no migrating by hand.
>   - It is read first, edits and all; one that does not read (a refused value, a SIF type CAESAR 15.01 does not have) is left as it was and the problems are listed.
>   - The new file replaces the old in one step, and the old stays beside it as `x.xlsx.bak` (the next refresh overwrites it).
>   - **What was typed stays as typed** (2026-11-03): every input cell goes back as it was, a formula as its formula (live node numbers, a delta worked out from other cells or another sheet; Excel's filled-down shared formulas expanded per cell) and an entry as entered (`LR` stays `LR`, a blank stays blank). Only the sheets, checks, highlights, hidden sheets and the base model around them are rebuilt (`PipeWorkbook.RefreshBytes`).
>     - Columns are matched by header, the unit in brackets aside. A column this version moved takes its cells along, and every reference to it (other sheets', whole-column ones too) is rewritten (`XlsxFormulaRefs`). A column this version adds is filled from the model on the rows the model came from, or left blank if those rows cannot be matched.
>     - Your own columns go after Pluto's, your own sheets are copied as values and formulas, and Settings keep their typed values by name. Your own formatting is only in the `.bak`.
>     - Checked on Linux: a refreshed workbook with `LR` radii, a delta formula, a delta from a sheet of its own, an extra column, a moved column and a shared formula reads to the same model, and every cell matches the original except the fingerprint.
>   - The edits become the workbook's base, as after `-Xlsx`: the next read lists only what changed since the refresh. Settings keeps the original source; the launchers are written again.
>   - Excel holds an open workbook: close it first. The Start sheet's run block carries the command.
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
> - **Launchers beside the workbook** (2026-10-23, `PipeWorkbookRun`): `-Xlsx` and `-NewWorkbook` also write `<name> - preview.cmd` (`-Preview -Watch`) and `<name> - cii.cmd` (`-Cii -Preview`, then waits so the report can be read). They find the workbook beside themselves (`%~dp0`); the runner's path is the one at write time.
>   - Outputs go beside the workbook under its name. A workbook made from a `.cii` of the same name in the same folder writes to `<name>_edit` instead, so the CAESAR job it came from is never written over.
>   - Why not buttons in Excel: they need macros (an `.xlsm`), which production machines block, and the workbook would stop being plain data.
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
>     - with a warning for a value not on the list: element type, bend type, restraint type, SIF type (by name, `3 - welding tee`, as CAESAR lists them), load vector (forces, displacements, uniform loads and an API 650 nozzle's growth), In g's, wind type, joint frame, vertical axis, nozzle type, vessel type, reinforcing, hanger free codes, short-range springs, seam weld, hanger table (CAESAR's whole list, `1 - ANVIL` to `39 - Rilco`; blank = the job's default);
>     - free entry allowed: Section, Process, joint kind;
>     - the bend radius: a dropdown of `LR`, `SR`, `3D`, `5D` that takes a radius (a number) too; `Long` and `Short` are read as well; anything else turns red;
>     - the material (Sections, Elements, a nozzle's vessel material): a dropdown of the common ones (`106 - A106 Grade B`) that takes any number from CAESAR's database too; anything that is neither turns red and is counted on Start.
>   - **Not a dropdown:** the multi-load option (a whole number, as CAESAR numbers it).
> - **Highlighting (conditional formatting):**
>   - **Red** = the build would refuse it:
>     - text or an error where a number belongs;
>     - a value not on its list, an unknown Section / Process, an unknown element Id or range (Uniform, Wind);
>     - a missing From / To, From = To, a missing section on the first row;
>     - a reducer without OD2 / Wall2, bend nodes without `angle:node`;
>     - a bend node number used twice: on another bend, or also an element's From / To (CAESAR stops on it);
>     - CAESAR's other errors a sheet can see: a bend outside 5–95° or a node on it within 5° of its far end or another, corrosion as thick as the wall, a user SIF under 1, a SIF type CAESAR 15.01 does not have (past 13), a node above 8,000,000, a displacement where a restraint already holds the node, an incomplete expansion joint, a WRC 297 or PD 5500 nozzle's stiffeners closer than its radius, a custom nozzle without its vessel material ([[vault/arms/caesar-checks|caesar-checks]]);
>     - a vector outside 1–9, the same node and vector twice;
>     - a duplicate section or process name, a cosine above 1.
>   - **Amber** = worth a look: a node no element has (bend nodes count), a copied Id, a zero-length element; and what CAESAR warns about: a corner with no bend, a tee where other than three pipes meet, D/t over 100, an expansion joint without torsion stiffness or with both transverse and bending, a rigid with no weight, a temperature under 0.05 (read as an expansion), g's and force per length both used, a designed hanger at a restrained or displaced node, a nozzle node a restraint, displacement or hanger also uses, an API 650 tank under 120 ft or its growth in a vector displacements use; and a bend node within 5° of the near end, a WRC 297 or PD 5500 nozzle without its vessel material.
>   - **Gaps** (2026-11-03): a row whose end lands away from where an earlier row put its To node turns its **To** amber (red past 1 in, where CAESAR stops, 44E); a From node whose Coords entry is away from where the pipe reaches it turns its **From** amber. Both from 0.001 in, worked out on PlotCalc with the 3D View's own positions (Gp, Gc), and counted on Start. CAESAR draws such a gap without a warning under 1 in. The review reports the same as `gap`. Only rows the 3D View placed (R2) are checked: a run off a bend node, or one hanging off runs placed late, is left to the review, which places those too.
>   - **Not flagged:** a bend not followed by the row leaving it. The `.cii` is written with that row moved up (8E), and the workbook stays as it is.
>   - **Long rules** are worked out per row on the hidden PlotCalc sheet, so every conditional-format formula stays short; the Start checks that look a node up do so per row on the hidden CheckCalc sheet.
>   - **A rule finds its PlotCalc row by `ROW()`** (`INDEX(PlotCalc!$I:$I,ROW())`), never by naming the cell (2026-11-01): Excel keeps a named cell when a row is inserted above the rule's first row, and every highlight then fell one row low (a rigid's missing weight shown on the row below it).
> - **Build it from here** (Start sheet, under the steps): links to the two launchers (relative, so they follow the folder), to Pluto's viewer page and to the folder, and the PowerShell command each launcher runs, built from `CELL("filename")` so it names the workbook where it is now (the path at write time where `CELL` gives none, e.g. in LibreOffice). Excel asks before it follows a link to a `.cmd` file; where policy stops it, double-click the file in the folder.
> - **Start's checks** count those cells live: error values; text where a number belongs (per sheet); rows without From / To; unknown names and types; unknown element Ids; vectors outside 1–9; nodes on no element; bend node numbers used twice; Ids used twice; and each CAESAR rule above. Each shows OK, check (amber) or fix (red).
> - **Kept as before:** the input sheets keep their columns, values and order (node numbers are formulas carrying their values, see Node numbers), so the reader is unchanged. Start, 3D View, Nodes, Carried and the hidden sheets are never read. Only the input sheets' cells are read, so the plot's formulas never trip the "formula with no saved value" check. The workbook asks Excel to calculate everything when it opens.

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
>   - **Columns:** each Elements column is read on its own (`INDEX(Elements!$D:$D,ROW())`), so PlotCalc row r stays Elements row r when rows are inserted, Excel's own adjustment follows moved columns, and an edit recalculates only what reads that column.
>   - **Positions** follow CAESAR's rules. A From node is where an element above reached it, else its Coords entry, else where the element above ended. A bend's element runs to the corner and the next element starts there, trimmed by the bend's tangent.
>   - **Backward branches:** a piece of the model that meets an already placed piece further down the sheet (a branch entered from its far end) is moved onto it, in two passes.
>   - **Projection:** azimuth, then elevation, with Z-up jobs turned first. The chart is square and a hidden square frame keeps both scales equal.
> - **Limits:**
>   - nodes on a bend's curve (M, angle nodes) are not drawn;
>   - a CAESAR code that is no radius is drawn as a corner, as CAESAR treats it;
>   - the formula rows end 300 elements past the job's count, so a much longer model needs the workbook written again (`-Xlsx`);
>   - where a loop of the model does not close, the place it is closed may differ from Pluto's viewer by that gap;
>   - the workbook is about 0.4–0.8 MB (the formulas).
> - **Speed** (2026-10-29), measured in LibreOffice on a 1,345-element line (Excel is faster and uses every core):
>
>   | | before | after |
>   |---|---|---|
>   | open (everything calculated) | 37 s | 14 s |
>   | full recalculation | 18.7 s | 6.4 s |
>   | edit a DX | 14.1 s | 1.9 s |
>   | edit a name, a temperature, a material | 15.4 s | 0.1 s |
>
>   - **Why it was slow:** every PlotCalc row read Elements through one reference to the whole sheet, so any edit there recalculated the whole view; each row scanned the rows above five times for the same node; two Start checks compared every row of six node sheets with every element.
>   - **Now:** one column per reference, one shared lookup per row (`mT`, `mF`), IF where AND / OR would work out a costly branch they do not need, and the node-lookup checks per row on CheckCalc.
>   - **Formulas of your own:** OFFSET and INDIRECT are volatile (every edit anywhere recalculates them and everything that reads them); `INDEX(column,ROW()-1)` does the same without. For a long editing session, Formulas → Calculation Options → Manual and F9 to see the result: Excel still calculates before it saves, so the values Pluto reads are current.
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
