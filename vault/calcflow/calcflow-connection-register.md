---
title: CalcFlow connection register and the plan dry run
status: draft
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow connection register and plan

The **connection register** (`Connection_Register.xlsx`) is the one workbook the engineer edits per project. It holds which connection type every member end is, which member it frames into, groups and overrides, and the load cases in scope.

**`plan`** is the dry run. From the analysis package, the calc library and the register, it resolves every cell of every calc, names every gap, and needs no Excel. It is CalcFlow's config / setup milestone (Phase 2).

The code is `scripts/calcflow/cs/Register.cs`, `Package.cs` and `Plan.cs`, run through `CalcFlow.ps1 register-new` / `plan`. Built and tested 2026-11-06 on the fixture project. **Not yet run on a real model.**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 register-new -Package "<project dir>\calcflow\package\MODEL-A\R03" -Library "<library folder>" -Out "<project dir>\Connection_Register.xlsx"
# give each connection that needs a calc its Type (check Support), save in Excel, then:
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 plan -Package "<project dir>\calcflow\package\MODEL-A\R03" -Library "<library folder>" -Register "<project dir>\Connection_Register.xlsx" -Templates "<templates folder>" -Out "<project dir>\calcflow\plan"
```

**Exit codes:** 0 = a complete plan; 1 = gaps, each listed (the files are still written); 2 = bad arguments. Output inside the repo is refused (it is project data).

> [!info]- The register's tables
> **`tblConnections`:** one row per member end at a joint, keyed by Node, Member and End; the id is `J<node>-M<member><end>` (`J3-M2B`).
> - **Blue (yours):**
>   - **Type:** a library type; blank = no calc.
>   - **Support:** the member it frames into, i.e. the type's `support` slot.
>   - **Other:** more slots, as `brace=12:A; gusset=30`.
>   - **Group:** connections sharing one calc, on the union of their demands.
>   - **Disposition:** Excluded / Accepted / Attention / Hold.
>   - **Comment.**
> - **Grey (the tool's):** Role, Section, Status, Status Detail, Max DCR, Governing Check, Governing Combo, Calc, Design Rev. These are filled from Phase 3 on.
>
> **The other tables:**
>
> | Table | What it holds |
> |---|---|
> | `tblOverrides` | Connection or Group, Step (blank = all), Param, Value, Note: a parameter for one connection. It wins over the type's default. |
> | `tblCombos` | LC, Title, Kind, Use, Basis: the cases in scope. Seeded with the combinations and repeat loads as Y (every case when there are none). A template with a Basis takes only cases of its basis. |
> | `tblSettings` | DcrLimit, GoverningK, Package (information) |
>
> **`register-new`:**
> - It seeds the register from a package, without Excel, and never overwrites a register that exists. Once saved, only Excel writes it; publishing grey columns through COM is Phase 4.
> - It guesses each Support: for a beam or a brace, the column at the joint (else a beam); for a column, a beam. It prefers a member that runs through the joint and never takes the connection's own continuation.
> - On the fixture, both beam ends get their columns, the brace gets the column, and a column base gets none.

> [!info]- What plan does, per calc and step
> 1. **The calcs:** one per register row with a Type (Excluded skips it), or one per Group. A group's types must agree, and so must its once-only inputs: a W14X90 column cannot share a base plate calc with a W8X31 post.
> 2. **The template:** found, pinned and guarded, checked once ([[vault/calcflow/calcflow-calc-library|calcflow-calc-library]]). A failing template blocks the calcs that use it.
> 3. **Once-only inputs:** every binding resolved, and units converted (kip-in ↔ kip-ft, in ↔ ft, psi ↔ ksi). Each cell records its **source**, e.g.:
>    - `section W24X55 d`;
>    - `section W14X90 tf` (through the support slot);
>    - `type BC-WELD default`;
>    - `override (J1-M4A step 2)`.
> 4. **The demand:**
>    1. the slot's printed end forces for the cases in scope, in end axes;
>    2. through the step's frame;
>    3. converted to kip and kip-in;
>    4. reduced by the demand mode.
>
>    A member end without printed forces for a case in scope is an error.
> 5. **Per-case inputs:** every demand row's `load:` / `case:` cells, in each cell's unit.
>
> **Written to `-Out`:**
>
> | File | Columns |
> |---|---|
> | `plan.csv` | Calc, Type, Step, Template, Sheet, Cell, Input, Binding, Value, Unit, Source: the once-only cells |
> | `cases.csv` | Calc, Step, Template, Row, LC, Connection, Sheet, Cell, Binding, Value, Unit: the per-case cells |
> | `demands.csv` | Calc, Step, Template, Mode, Row, LC, Connection, Fx…Mz (template axes, kip / kip-in), Note (which extreme) |
> | `calcs.csv` | Calc, Type, Group, Connections, Cells, Cases, Status (Ready / Gaps / Blocked), Problems |
> | `problems.csv` | Severity, Calc, Code, Message |
>
> The Excel stage (Phase 3) takes these files as they are.

> [!info]- The problems it names
> **Errors:**
> - **Register and type:** `TYPE_UNKNOWN`, `GROUP_TYPES`, `GROUP_DIFFERS`.
> - **Inputs:** `UNBOUND` (a required input with no binding); `UNRESOLVED` (a parameter with no value, a slot not given, a section not in tblSections, a blank register column, a unit mismatch).
> - **Demand:** `SLOT`, `FORCES_MISSING`, `NO_CASES`, `CASE_UNKNOWN`.
> - **Templates:** `TEMPLATE_MISSING`, `TEMPLATE_CHANGED`, `GUARD_MOVED`, `INPUT_IS_FORMULA`, `OUTPUT_NOT_FORMULA`.
> - **Package:** `NOT_IN_PACKAGE` (a register row the package lost: renumbered?).
> - **Library and register files:** `LIBRARY` and `REGISTER` (their own errors).
>
> **Warnings:**
> - `NEW_CONNECTIONS`: member ends in the package but not in the register (the new-connection gate).
> - `UNSIGNED_CASES`: SRSS / ABS combinations are in scope. Their end forces are formed from magnitudes, so their signs and concurrency are not those of a real load.
> - `BASIS_NOT_SET`, `CHUNKS`, `NO_GUARD`, `NO_COMBOS`.
> - The library's unconfirmed frames.

> [!info]- The DCR summary (to come with the Excel stage: Phase 3 / 4)
> The structure's connection DCRs will be summarised **per member end, with a joint rollup**, in a generated workbook (`reports\Connection_Summary_<date>.xlsx`, no Excel needed to write it):
> - **Connections:** one row per register row with a Type.
>   - Columns: Connection, Joint, Member, End, Type, Group / Calc, Status.
>   - Then **one column per Check** (the library's `tblOutputs.Check` names: Weld, Base plate, …) with that check's largest DCR over the calc's cases.
>   - Then Max DCR, Governing Check, Governing Case and, for a group, the governing connection.
> - **Joints:** one row per joint, with the worst connection there, its DCR, check and case, and how many connections are OK / over the limit / not run.
> - **The same values go into the register's grey columns** (through Excel, Phase 4), and to the viewer as connection glyphs coloured by DCR (Phase 5).

> [!info]- Verification (2026-11-06, Linux)
> **The fixture project:**
> - the frame-demo `.anl` → package;
> - the example library on the generic templates;
> - a register giving five member ends the three example types:
>   - two column bases as group `G-BP`;
>   - two beam ends into columns (`BC-WELD`, base metal from the support slot);
>   - a brace with a step-2 override (`BRC-GUSSET`).
>
> **Result:** 4 calcs ready, 80 once-only cells, 14 demand rows, 84 per-case cells, 0 errors.
>
> **The tests** (`tests/Plan.Tests.ps1`, 10) check:
> - the package read back;
> - the register seed and its support guesses;
> - every source;
> - the end-axes sign of both base plate columns;
> - the four demand modes;
> - each gap: unknown type, missing parameter, missing slot, a moved guard (blocks the calc), an unpinned revision, new connections, a group that differs;
> - the CLI's exit codes.
>
> The suite has 51 tests in all, which pass under PowerShell 7.
>
> **Still to do on the production machine:** a Windows PowerShell 5.1 run, and the register opened and saved in Excel.
