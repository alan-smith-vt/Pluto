---
title: CalcFlow calc library and harvest (what each locked template takes and gives)
status: draft
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow calc library and harvest

The **calc library** says, once per locked calc template:
- which cells it takes, each guarded by the label beside it, with its unit and where its value comes from;
- which results are read back;
- which **connection types** use the templates, and how (steps, slots, sign maps, parameters).

**Harvest** learns a template's layout the first time it is seen and drafts those rows, so the map is not typed by hand.

The code is in `scripts/calcflow/cs/` (`Template.cs`, `Harvest.cs`, `Library.cs`, `Units.cs`), run through `scripts/calcflow/CalcFlow.ps1`. The example library is `scripts/calcflow/examples/library/text/` (three types on the generic weld and base plate templates). Built 2026-11-06 and tested on generic templates; **the real templates are not in this repo and have not been harvested yet.**

```powershell
# once per template (the production machine): learn the layouts, draft the rows
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 harvest -Templates "<templates folder>" -Out "<library folder>\harvest.xlsx"
# start the library from the drafts, edit it in Excel, then export the canonical text the tool runs from
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 library-new -Library "<library folder>" -Harvest "<library folder>\harvest.xlsx"
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 library-export -Library "<library folder>"
```

> [!info]- Learning a template once (harvest)
> `harvest` reads each `.xlsx` without Excel (`TemplateBook`). It sees:
> - every cell's cached value and formula, with shared formulas shifted to each cell and array and data-table ranges marked;
> - the style's locked flag and the sheet's protection;
> - defined names, data validations, tables;
> - and **which cells the formulas read**: cells, ranges, other sheets, defined names, table columns (`NodeCases[Fx]`).
>
> **What it drafts:**
> - **Inputs:**
>   - a constant read by a formula, with its own label on its row and not part of a grid of numbers (a lookup table);
>   - a cell with a drop-down;
>   - an unlocked cell on a protected sheet;
>   - a cell a defined name points at (the name is used as the Cell);
>   - a blank cell a formula reads, with a label beside it (an optional input);
>   - one row per column of an Excel table (a case table).
> - **Outputs:**
>   - a formula whose row title, row label, name or formula says DCR, D/C, ratio, unity or interaction;
>   - an `OK` / `NG` status formula;
>   - a named cell that reads as a solver check (converged, singular).
> - **Guard:** each row's nearest text to the left (else just above). The unit is taken from the label's brackets (`wleg (in.)` → in).
>
> **What it writes:** `harvest.xlsx`, with its tables in the library's columns (so rows paste straight in, or `library-new -Harvest` starts a library from them), plus a **Layout** table of every cell for reading the template by eye.
>
> **On the generic templates:**
> - the weld tool's rectangular tab gives exactly its 22 inputs (16 geometry and material cells, 6 loads) and its 3 DCRs;
> - the base plate tool gives its named inputs and `dcr_all`, plus `Converged` and `Singular` as health outputs.
>
> **What the engineer then does:**
> - fill in the **Binding** of every input the calc needs;
> - delete the drafts that are not wanted;
> - name each DCR's **Check** (the summary column it goes in);
> - set health outputs' **Expect**;
> - split a multi-tab tool into one Template id per tab: a tab is one calc, and the rows keep the same File and Sha256.

> [!info]- The tables (`CalcFlow_Library.xlsx` ↔ `text\*.csv`)
> | Table | Columns | Notes |
> |---|---|---|
> | `tblTemplates` | Template, File, Sha256, Shape, MaxCases, Basis, Calculation, Notes | Shape: `single_case` (one case in fixed cells, looped) / `case_table` (a block of case rows; MaxCases per run) / `multi_connection` (not planned yet). Basis LRFD / ASD: the template takes only cases of its basis. |
> | `tblInputs` | Template, Sheet, Cell, GuardCell, GuardText, Unit, PerCase, Binding, Required, Notes (+ harvest's Example, Kind, List, Description, Found) | Cell is an address or a defined name. Required = N: a blank Binding leaves the template's own value. |
> | `tblOutputs` | Template, Sheet, Cell, GuardCell, GuardText, Output, Kind, Check, Expect, PerCase, Notes | Kind: `dcr` (summarised under Check) / `status` / `health` (must equal Expect, e.g. `TRUE`) / `value`. |
> | `tblFrames` | Frame, Fx … Mz, Mirror, Action, Confirmed, Notes | Sign maps from the end axes (below) to the template's axes. IDENTITY is built in. |
> | `tblTypes` | Type, Description, Slots, Notes | Slots: the member ends besides this one that bindings name (e.g. `support`). |
> | `tblSteps` | Type, Step, Template, Demand, Slot, Frame, Notes | The calcs a type runs, in order. |
> | `tblParams` | Type, Step, Param, Default, Unit, Notes | A Step row wins over a blank-Step row. A register override wins over both. |
> | `tblSections` | Section, Prop, Value, Unit, Notes | For `fn:prop(slot, prop)`. |
>
> **The round trip:**
> - The workbook is edited in Excel (blue headers are filled in by a person; grey ones are harvest's notes). A Guide sheet lists the bindings, units, demand modes and the sign rules.
> - `library-export` writes one CSV per table (the schema's columns first, extras kept, rows sorted by key), plus `text\_source.txt` with the workbook's SHA-256.
> - **The tool runs from the text and refuses a workbook that differs from its last export.**
> - `library-new -FromText` rebuilds a workbook from the text. Tested: a round trip gives the text back byte for byte.

> [!info]- Bindings (where an input's value comes from)
> | Binding | Value |
> |---|---|
> | `const:<v>` | a literal (`const:LRFD`, `const:0.707`) |
> | `param:<name>` | the connection's override (register `tblOverrides`, by connection or group, step or all steps), else the type's default |
> | `reg:<column>` | a column of the connection's register row |
> | `pkg:<slot>.<field>` | a package fact. Slots: `this` (the connection's member end), `node` (its joint), or a type slot. Fields: Member, End, Node, Section, Family, Role, Released, Kind, Beta, Length; `node.X/Y/Z/Support`. |
> | `load:<Fx…Mz>` | the demand component in the template's axes, per case (PerCase = Y) |
> | `case:<id\|title\|kind>` | the load case, per case |
> | `fn:prop(slot,prop)` `fn:len(slot)` `fn:angle(a,b)` `fn:section(slot)` `fn:family(slot)` `fn:beta(slot)` | fixed, tested functions: no formula engine ([[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]]) |
>
> **Units:** in ft mm cm m · in2 in4 · kip lbf kN N mton · kip-in kip-ft lbf-in lbf-ft kN-m N-m N-mm · ksi psi MPa · deg rad.
> - A value converts only within its dimension. A moment never goes into a force cell, and a length never goes into a cell without a Unit.
> - The checks run when the library loads (`Bindings.Check`): the grammar, PerCase against the kind, and the cell's dimension.

> [!info]- Sign maps: end axes and frames
> **End axes.** A member end's forces are taken in that end's own axes:
> - x along the member, pointing **away from the joint**;
> - y the member's local y (BETA included);
> - z = x × y.
>
> At the start joint (end A) these are STAAD's member axes. At the end joint (end B) they are STAAD's axes turned 180° about y, so Fx, Fz, Mx and Mz change sign. The sign stays STAAD's: the force on the member at that end.
>
> **Why:** a column drawn top-down otherwise shows uplift where it bears ([[vault/decisions/2026-11-06-calcflow-end-axes|2026-11-06-calcflow-end-axes]]).
>
> **Frames** map end axes to a template's axes, e.g. `Fx = -Fz, Fy = +Fy, Fz = +Fx`.
> - The forces must be a signed permutation. The moments must follow the same permutation; a mirror (determinant −1, Mirror = Y) negates them. A wrong moment sign is refused with the sign it should have.
> - Action `on_connection` negates all six: the force on the connection.
> - A frame not marked **Confirmed = Y** gives a warning wherever it is used.
> - The example library's frames are illustrative and unconfirmed.

> [!info]- Demand modes (tblSteps.Demand)
> - `per_combo`: every case in scope.
> - `concurrent_envelope`: the max and the min case of each of the six components, each with its concurrent forces. At most 12 rows, deduplicated, each noted ("max Fx, min Fz").
> - `abs_envelope`: one row of the largest magnitude of each component with its sign. **Not concurrent: conservative.**
> - `governing_set`: the concurrent envelope, plus the GoverningK cases (register setting, default 5) with the largest normalised sum Σ\|Fi\| / max\|Fi\|.
>
> A Group (register) reduces the union of its connections' demands, so one calc covers the worst of them.

> [!info]- Checked without Excel, before every plan
> - **The file:** found in the templates folder, and its SHA-256 equal to the pin. A different file is refused. If every guard still holds, the message gives the new SHA-256 to pin.
> - **Inputs:** every input cell (or name) exists and holds no formula, so writing it cannot overwrite the template's logic.
> - **Outputs:** every output holds a formula.
> - **Guards:** every guard cell reads its GuardText (trimmed, whitespace runs as one, ignoring case). A moved layout names the cell and what it now reads.
