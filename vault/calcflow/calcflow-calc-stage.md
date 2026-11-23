---
title: CalcFlow calc stage (run) and the connection summary
status: draft
created: 2026-11-23
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow calc stage and connection summary

**`run`** calculates the plan's due calcs and keeps their results. For every calc step, it writes the planned cells into copies of the locked template and has an engine recalculate them. It then reads the DCRs back, checks that each case is valid, and keeps the calculated copy that governs each check as the record. **`summary`** writes the structure's connection DCRs per member end, with a joint rollup, in one generated workbook.

The engine is **Excel where it is installed, else LibreOffice** (open source, headless). Each record names the engine that calculated it ([[vault/decisions/2026-11-23-calcflow-engines|2026-11-23-calcflow-engines]]).

The code is `scripts/calcflow/cs/Run.cs`, `Engines.cs`, `LibreOffice.cs` and `Summary.cs`, plus `stages/Recalc-Workbooks.ps1` for Excel, run through `CalcFlow.ps1 run` / `summary`. Built 2026-11-23. Tested on the fixture project with LibreOffice and the tests' engine; **the Excel path has not run yet** (it needs Windows and Excel), and no real model has been run either.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 run -Plan "<project dir>\calcflow\plan"
# options: -Work <folder> (default: the plan folder's parent)  -Rev R03 (default: the package folder's name)
#          -Engine auto|excel|libreoffice  -All  -Calc <ids>  -Visible (Excel)  -KeepRun (keep the run's copies)
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 summary -Plan "<project dir>\calcflow\plan"
```

**Exit codes:**
- `run`: 0 = every step ran OK (DCRs over the limit are not errors); 1 = a step failed or was invalid; 2 = bad arguments or a stale plan.
- `summary`: 0, or 1 while a step is failed or invalid.

> [!info]- What run does
> 1. **The plan must be current.** `plan` now also writes `plan-manifest.txt`: the package, library text and register it was made from, with their SHA-256s. `run` refuses the plan when one has changed ("plan again"). The register counts too, because a new Type or Group changes the calcs.
> 2. **Jobs:** one per ready (calc, step). `CalcJob.FromPlan` opens each template once and checks it is the pinned file. It resolves every cell and defined name to an address.
>    - A **single_case** template is evaluated once per demand row.
>    - A **case_table** template takes MaxCases rows per evaluation; row k's per-case cells go k−1 rows down.
>    - A value is written as **text where the template's cell holds text**, else as a number when it reads as one; blank stays blank.
> 3. **What is due:** never run; failed last time; inputs changed; a record missing; a new revision. `-All` takes every step and `-Calc` limits the run to some calcs. An invalid step is not rerun on its own: its inputs give the same answer. Fix the cause, then use `-All` or `-Calc`.
>    - The **input hash** is taken from the plan rows alone (the template as pinned, its outputs, the cells, the per-case cells, the demand rows). The summary can therefore tell a current result from a stale one without opening a template.
> 4. **A new revision with unchanged inputs is carried forward.** The records are copied into the new revision's folder and stamped `carriedFrom`; nothing is calculated.
> 5. **Batches:** each evaluation is a copy of the template with its cells written at the XML level (`XlsxPatch`):
>    - numbers as values, text inline;
>    - a blank cell created where it is missing;
>    - the calc chain dropped and `fullCalcOnLoad` set.
>
>    The engine recalculates a batch of copies and saves them. The outputs are then read back with the template reader.
> 6. **Each job is saved as it finishes** (results, checks, state, records), so a run that is stopped keeps every job it finished.

> [!info]- Validity, checks and records
> **A case is valid when:**
> - every **health** output equals its Expect (TRUE / 1 / Y compare as booleans; LibreOffice saves a formula's TRUE as 1);
> - every **dcr** output is a finite number (an error value or a blank is invalid);
> - every **status** output is not blank.
>
> A case the engine did not calculate is **not calculated**, and its step is FAILED.
>
> **Per check:**
> - A check is a dcr output's Check, else its label.
> - Its result is the largest DCR over the valid cases, with the case, row and connection it came from.
> - A group gets the same per connection, from that connection's own demand rows (an envelope row with no connection counts for every connection).
> - A **status** output that is not OK-like (OK, PASS, Y, YES, TRUE, ADEQUATE, ACCEPTABLE, SATISFACTORY) marks its check **NG**. This is a default, not one of the copied engineering rules: confirm it against the real templates.
>
> **Records** (`records\<rev>\<calc>\<step>-<template>-<checks>.xlsx`):
> - Each record is the calculated copy the governing case was read from, so it shows exactly what was read.
> - There is one per check; checks with the same governing case share a file (`1-WG-RECT-Weld+Base metal.xlsx`).
> - Beside each is `<record>.calcflow.json`, holding:
>   - the calc, type, step, connections, template and its SHA-256;
>   - the engine and its version;
>   - the rev, package and input hash;
>   - the record's case(s) and the checks it governs, with their DCRs.
> - An invalid step keeps its first invalid case as `<step>-<template>-invalid.xlsx`, with the reason in its stamp.
> - A rerun replaces that step's records in the revision's folder; other revisions' folders stay.
>
> **Written to `-Work`:**
>
> | File | What it holds |
> |---|---|
> | `data\results\<calc>__s<step>.csv` | every case: Row, LC, Connection, Calculated, Valid, Why, and each output's value |
> | `data\results\<calc>__s<step>.checks.csv` | per check and connection: Max DCR, Status (NG), LC, Row, valid / invalid cases, Record |
> | `data\calc-state.csv` | per (calc, step): Template, Rev, Engine, Engine Version, Input Hash, Template Sha256, Last Run, Result (OK / INVALID / FAILED), Cases, Invalid, Failed, Seconds, Records, Carried From, Message |
> | `records\<rev>\...` | above |
> | `reports\Connection_Summary_<date>.xlsx` | below (written after every run) |

> [!info]- The engines
> - **LibreOffice** (`LibreOfficeEngine`, any OS):
>   - one `soffice --headless --convert-to xlsx` per batch (200 copies);
>   - its own profile (`<work>\run\libreoffice-profile`, never the engineer's), set to recalculate every formula on load;
>   - a time limit that stops it;
>   - `.xlsm` is kept with its macros.
>
>   It is found on PATH, in `/usr/bin` or in `Program Files\LibreOffice\program`.
> - **Excel** (Windows): `stages\Recalc-Workbooks.ps1`, a Windows PowerShell 5.1 STA child under `Invoke-CfStage`, 50 copies per batch.
>   - Each copy is opened **read-only**, so a write-reservation password never prompts.
>   - It is calculated with `CalculateFull` and saved under the same name in the batch's out folder.
>   - A heartbeat before every file lets the parent stop exactly that Excel if it stops answering.
>   - The template itself is never opened.
> - **Auto:** Excel when its COM class is registered, else LibreOffice, else a refusal that names both.
> - **The tests' engine** (`ScriptedEngine`) computes outputs from each copy's cells with a delegate and writes them in as values.

> [!info]- The connection summary (`reports\Connection_Summary_<date>.xlsx`)
> It is written without Excel, in the CalcFlow colours. A second one written the same day, while the first is open, is saved as `_2`, `_3` and so on.
> - **Connections:** one row per member end with a Type, and every Excluded one.
>   - Columns: Connection, Joint, Member, End, Type, Calc, Group, Disposition, Status, Max DCR, Governing Check, Governing Case, Step, **one column per check**, Why, Records, Engine, Rev, Last Run.
>   - A group's connections each show the DCRs of their own demand rows, and list the calc's records.
> - **Joints:** the worst connection at each joint (over the limit, then invalid, failed, gaps, not run, OK), its DCR, and the counts.
> - **Calcs:** one row per calc step and its last run.
> - **Problems:** the plan's problems, and every failed or invalid step.
>
> **Status ladder (the first that applies):**
> 1. **Excluded** (Disposition);
> 2. **Gaps / Blocked** (the plan);
> 3. **Failed**;
> 4. **Not run**: never run, inputs changed since, or last run on another revision;
> 5. **Invalid**;
> 6. **Over limit**: a DCR above the register's `DcrLimit`, or an NG status;
> 7. **OK**.
>
> **Colours:**
> - statuses: OK green, Over limit and Failed red, Invalid orange, Not run / Gaps / Blocked grey, Excluded light grey;
> - DCRs above the limit in red;
> - Problems: errors red, warnings amber.
>
> The register's charcoal columns stay empty until Phase 4 (written through Excel).

> [!info]- Checks with open-source tools (development side)
> - **The Open XML SDK** (Microsoft, MIT; `tests/tools/OoxmlCheck`, built by `tests/Tools.ps1` with the dotnet SDK) validates every workbook a test run writes against the schema Excel reads. A copy with its cells written must add no schema error to its template's own (the base plate fixture's `styles.xml` has 17 of its own).
> - **LibreOffice** opens every workbook a test run writes; a load error fails the test.
> - **Previews:** LibreOffice prints to PDF and poppler's `pdftoppm` renders PNG pages (`Export-CfPreview`). This is how the colours were checked.
>
> Each check says "skipped" where its tool is missing (for example on the production machine).

> [!info]- Verification (2026-11-23, Linux, LibreOffice 24.2)
> **The fixture project** (as in [[vault/calcflow/calcflow-connection-register|calcflow-connection-register]]):
> - `plan` → 5 calc steps;
> - `run -Engine libreoffice`: 14 copies in one batch, about 10 s, all 5 steps OK.
>
> | Calc step | Check | Max DCR | Case |
> |---|---|---|---|
> | G-BP 1 (BP) | Base plate | 1.200148 | 102 |
> | J1-M4A 1 (WG-RECT) | Weld | 0.426466 | 3 |
> | J1-M4A 2 (WG-RECT) | Weld | 0.157950 | 3 |
> | J2-M2A 1 (WG-I) | Weld | 0.091048 | 3 |
> | J3-M2B 1 (WG-I) | Weld | 0.286164 | 3 |
>
> - **Cross-checked against EngCalc-Templates' own Python references**, from each record's own input cells (a development-side script, not in the repo):
>   - the WG-RECT records agree with `weld_ref.check_rect` to 5e-16;
>   - the base plate record agrees with `baseplate_ref.analyze` (dcr_all, dcr_plate, dcr_anch, Converged) to 2e-15.
>   - The WG-I records were not cross-checked.
> - **The base plate's 1.200 comes from case 102, an SRSS combination.** Its Fz is +20.5 kip (uplift), which a magnitude combination cannot mean. This is exactly what `UNSIGNED_CASES` warns about: on a real job, take SRSS / ABS cases out of scope for base plates, or check them by hand.
> - **The second run:** nothing due.
>
> **Tests:**
> - `Run.Tests.ps1` (11, tests' engine):
>   - jobs and writes;
>   - the DCR of every case from its own written loads;
>   - governing per check and per group connection;
>   - records and stamps;
>   - what is due;
>   - carry-forward;
>   - shared and separate records;
>   - invalid and failed cases;
>   - case-table rows;
>   - a stale plan refused;
>   - patched copies schema-clean.
> - `Summary.Tests.ps1` (5): every status at once, the group split, the joints, the colours, the CLI.
> - `Engine.Tests.ps1` (2): LibreOffice end to end, and XlsxPatch.
> - `Excel.Tests.ps1`: the same project through Excel, 1e-6 from LibreOffice's DCRs. **It runs only on the production machine.**
>
> The suite is 76 tests, 0 failed; plus the strict C# 5 compile of CalcFlow alone and of lib + arms + CalcFlow.

> [!info]- Open points
> - **The Excel stage is untested.** Run `scripts\calcflow\tests\Run-Tests.ps1 -Filter Excel` on the production machine (Windows PowerShell 5.1, Excel).
> - **LibreOffice vs Excel:** a real template may use a function or feature LibreOffice evaluates differently (iterative calculation, some array functions, macros). Run both engines once on each real template and compare; Excel.Tests does this for the generic ones.
> - **Status outputs:** the OK-like words (above) are a default to confirm.
> - **Disk:** a job keeps every case's calculated copy until it finishes (a `per_combo` step with many cases holds them all at once).
> - **Speed:** Excel opens one copy per case. That is fine for envelopes; a long `per_combo` step on Excel will be slow.
