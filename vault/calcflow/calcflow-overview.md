---
title: CalcFlow overview (the brainstorm's verdict, the design, the roadmap)
status: draft
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow overview

**The job.** The members of a steel frame are analysed and code-checked in STAAD.Pro. The connections are not. Each standard detail (beam to column, beam to beam, beam to gusset, beam or column to base plate, welds, bolts) is a **locked Excel workbook**, sometimes several per detail. Each needs its own inputs:
- some need member connectivity and the full end forces per load combination;
- some only need an envelope.

Today those inputs are carried from the model and the reference files into each workbook by hand, and the results are carried back out the same way.

**CalcFlow** sets up and runs those calcs from one analysis data set:
1. STAAD's `.anl` becomes an **analysis package**.
2. A **calc library** says what every locked template takes and where.
3. A **connection register** says which detail every connection is.
4. The tool fills a copy of each workbook, runs it in Excel, reads the results back, and keeps one status per connection.

This note is the outcome of the 2026-11-06 brainstorm. Three explorers read the Pluto STAAD lib, pipe-review and the calc templates, and one architect drafted the design. Step 1 is built: [[vault/calcflow/calcflow-staad-frame-reader|calcflow-staad-frame-reader]]. The decision is in [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]].

## The verdict on "one configuration workbook"

The instinct is right: it should be a table, not code. But one workbook would mix things that change at different times and belong to different people. They are split instead:

| Layer | Changes when | Who edits it | Form |
|---|---|---|---|
| **Calc library** | a template is revised, or the company standard changes | the template owner, the lead engineer | `CalcFlow_Library.xlsx`, edited in Excel. `library export` writes the canonical text (one sorted CSV per table), which is what the tool runs from and what a review diffs. The tool refuses to run when the workbook differs from its last export. A project pins a copy. |
| **Connection register** | every model revision | the project engineer | `Connection_Register.xlsx`, one row per connection, like pipe-review's support register. Blue columns are typed by the engineer; grey columns are written by the tool, through Excel. |
| **Analysis package** | every STAAD run | nobody: it is generated | one folder per model and revision, written from the `.anl` (step 1) |
| **Machine config** | rarely | the user | `calcflow.psd1`: paths and timeouts only. Engineering settings go on the register's Settings tab, where they are seen and hashed. |

The calc library holds two things:
- **Templates:** for each locked workbook (or tab), its file and SHA-256, its shape, its cell map with label guards, its outputs, and its sign map.
- **Connection types:** the recipes. A type lists its calc steps, each a template, plus parameter defaults such as bolt diameter, grade and plate thickness.

From these, the tool works out:
- which workbooks each connection needs;
- every value going into every cell;
- which calcs to rerun;
- and the statuses.

> [!info]- How a model update runs
> ```mermaid
> flowchart LR
>     A["staad\MODEL-A\R03\*.anl"] --> P(["package"]) --> K["analysis package<br/>nodes, members, end forces,<br/>joint index"]
>     L["CalcFlow_Library<br/>(templates, types)"] --> N(["plan (dry run)"])
>     R["Connection_Register.xlsx<br/>(type, ends, group, overrides)"] --> N
>     K --> N --> X(["Excel stage:<br/>fill a copy, calculate,<br/>read the results"]) --> S["results, statuses"]
>     S --> R
>     S --> W["reports\Review_Workbook.xlsx<br/>reports\Connection_Status_date.xlsx"]
>     S --> V["viewer overlay<br/>(connections by status)"]
> ```
> 1. **Save the new `.anl`** in `staad\<model>\<rev>\`, never over an old one.
> 2. **Run Update.** It writes the package, then checks the register. New connections stop the run (the new-connection gate): give each a Type (a suggested one is filled in), save, and run again.
> 3. **`plan` resolves every cell of every calc** and lists everything missing, before Excel opens. Only the calcs whose inputs changed run.
> 4. **Work the To Do list,** set dispositions in the register, run Refresh, and send the status report.

> [!info]- The ideas, ranked by how much tedium each removes
> 1. **One analysis package per STAAD revision.** It is the only source of inputs, so nothing is copied from a model or a file by hand.
> 2. **Connection types as recipes.** One assignment drives every workbook the type needs.
> 3. **Cell maps with label guards for the locked templates, drafted by `harvest`.** `harvest` reads the template's XML without Excel:
>    - unlocked cells, defined names, label text, validation lists.
>
>    Each mapped input names its label cell and the text expected there. A revised template whose cells moved is refused instead of being filled wrongly.
> 4. **The tool resolves demand modes.** A template sees only the cases it needs:
>    - `per_combo`: every combination;
>    - `concurrent_envelope`: the max and min of each component with its concurrent forces, at most 12 rows;
>    - `abs_envelope`;
>    - `governing_set`: the concurrent envelope, plus the top cases by a normalised proxy, plus listed cases.
> 5. **Typical groups.** One calc runs on the envelope of a group of like connections, and the status is still kept per connection. The architect's estimate for 800 connections × 2.5 steps × 150 combinations is about 17 h with every combination through every calc, and about 25 min with concurrent envelopes on about 100 groups. The seconds per case are to be measured in Phase 3.
> 6. **The `plan` dry run.** Every cell is resolved and every gap is named, with no Excel needed.
> 7. **Rerun only what changed.** pipe-review's design revs (`R03`, `R03+1`), input hashes (exactly what the calc consumes), recheck rules and status ladder do this.
> 8. **Rule-based typing.** A joint-index rule proposes a grey Suggested Type and the engineer confirms it. A **viewer overlay** shows the connections by status (sidecar glyphs at the member ends).

> [!info]- Considered and not chosen
> - **One flat configuration sheet.** The template maps, the company recipes and the project's assignments change at different times, and one sheet would make every edit a merge.
> - **Macros or external links in the templates.** The templates are locked, and macros are off in the Excel stage.
> - **A script per template.** It does not scale to dozens of templates; a data-driven cell map does.
> - **A formula engine in the bindings.** It would bring untested, unit-free arithmetic into the configuration. Instead, a fixed C# function library (`prop`, `family`, `angle`, `face` …), versioned in the input hash.
> - **Pluto superposing the combinations.** That is wrong for P-delta and tension-only members. The combinations are taken as STAAD printed them.
> - **A Python driver.** Python is not on the production machine; the C# 5 / PowerShell 5.1 rule holds.
> - **A tool sheet added to each record copy.** The record copy is the template plus its inputs only (the engineer's choice). Traceability lives in the tool's records and a JSON file beside the copy.

> [!info]- The calc library (spec sketch, Phase 2)
> - **Templates:** TemplateId, File, Tab, SHA-256, Shape, Rows / MaxCases, Units, Basis (ASD / LRFD), and the Excel-only features it uses (data tables, 365 functions, Iteration). The Shape varies by template:
>   - `single_case`: one connection, one case in fixed cells;
>   - `case_table`: one connection, a block of case rows;
>   - `multi_connection`: many connections in rows.
> - **Inputs:** Template, Tab, Cell (or a defined name), Guard Cell, Guard Text, Unit, PerCase, Binding. The bindings are:
>   - `const:LRFD`
>   - `param:bolt_dia` (a register override, else the type's default)
>   - `reg:<column>`
>   - `pkg:beam.Section`
>   - `load:Fx` (the demand in the template's frame)
>   - `case:name`
>   - `fn:prop(beam,d)`
> - **Outputs:** Cell, Guard, Kind (`dcr / value / health / text`), Check, and an expected value for solver-health flags (e.g. a base plate's `Converged` = TRUE).
> - **Frames:** signed-axis maps from member-local end forces to a template's axes. The determinant must be +1 unless Mirror = Y, which negates the moments. Action is `on_member` (STAAD's sign) or `on_connection`.
> - **Types:** TypeId (`BC-ST-4`), its slots (`support`, `beam`, `brace`), its steps (template + demand mode) and parameter defaults.
>
> Worked examples come from the weld group and base plate templates (EngCalc-Templates):
> - a weld tab's input `B14` guarded by `A14` = "Fx", and its output `F49` = "Governing DCR";
> - the base plate's defined names `Fz_k`, `tp`, `dcr_all`.

> [!info]- The connection register (spec sketch, Phase 2)
> - **`tblConnections`.**
>   - Blue: Conn Id, Node, Ends (`support=M-12:B; beam=M-40:A`), Type, Group, Disposition, Comment.
>   - Grey: Suggested Type, Suggested Group, Status, Status Detail, Max DCR, Governing Check, Combo, Design Rev, Records.
> - **`tblOverrides`** is a long table (Conn Id, Param, Value, Note), checked against the type's parameters.
> - **The other tables:** `tblGroups`, `tblCombos` (the combinations in scope, with their basis), `tblSettings` (DCR limit, tolerances), `tblDispositions`.
> - **Instance key:** `Node|Member:End`, or `Node` alone for a base plate. A renumbered model is re-matched by position and direction, and reported as renumbered.

> [!info]- Running a locked workbook (spec sketch, Phase 3)
> **The case loop** runs in a child Windows PowerShell 5.1 (STA) process with heartbeats and deadlines (`scripts/calcflow/Stage.ps1`, from pipe-review). A hung Excel is stopped, never one the engineer has open.
> 1. Check the template's hash and copy it.
> 2. Open the copy with alerts, events and macros off, and calculation manual.
> 3. Write the once-only inputs.
> 4. For each case: write its cells, `Calculate`, then read the outputs and health flags. An error value or a failed flag makes that case invalid.
> 5. Keep the maximum of each output with its combination.
> 6. Write the governing case, check that it reproduces, and save the record copy as `records\<conn|group>\<step>_<rev>.xlsx`.
>
> **Case-table templates** take the cases as one block write, chunked above their capacity. Data tables need a real Excel recalculation; that is to be confirmed on the production Excel.

> [!info]- Change detection, statuses, reports (spec sketch, Phase 4)
> - **Input hash:** the canonical cell writes of every case, plus the template SHA, the library rows used, the function versions and the demand mode.
> - **Status ladder** (first match wins): Excluded → Data issue (no member end in the package / type or slots unresolved / an input unresolved or a guard failed) → Needs attention (disposition) → Recheck → Calc invalid → OK (accepted) → Needs attention (DCR over the limit) → In progress → OK.
> - **Reports:**
>   - `Review_Workbook.xlsx`, with tabs To Do, Connections, Steps, Plan (every resolved cell with its source), Demands, Results, Changes, Design Revs and Run Log;
>   - `Connection_Status_<date>.xlsx`.
> - **A proposal that needs sign-off:** a load change beyond tolerance in *either* direction triggers a rerun. pipe-review reruns only on a rise, but less compression can raise a base plate's uplift DCR.

## Roadmap

| Phase | Scope | Exit |
|---|---|---|
| 0 | this note, the decision | done 2026-11-06 |
| **1** | **the STAAD frame reader and the analysis package** ([[vault/calcflow/calcflow-staad-frame-reader|calcflow-staad-frame-reader]]) | built and tested on a made-up fixture (2026-11-06). Still to do: one real `.anl` on the production machine, with five end forces spot-checked against STAAD. |
| 2 | the calc library (Excel + text export), the register, `harvest`, `plan` | **the config / setup milestone**: `plan` resolves every input of three connection types on a fixture project, and names every gap |
| 3 | the Excel stage (case loop, case tables, record copies) | the loop's DCRs equal a fixture's closed form; the governing case reproduces; a hung Excel is stopped; seconds per case measured |
| 4 | statuses, design revs, reports | a two-revision fixture (a load rise, a sign flip, a new joint) gives the expected status table |
| 5 | typing rules, the viewer overlay | every fixture joint gets the expected suggestion; the sidecar keeps the user's groups |
| 6 | groups, the combination pre-filter with its audit | `governing_set` reproduces `per_combo`'s governing DCR on a 200-combination fixture |

> [!info]- Open engineering questions (answers go here, and into a decision note)
> | Question | Answer |
> |---|---|
> | STAAD gives the force on the member at its end. Which way does each template want it (on the connection?), and what is positive (base plate Fz > 0 = uplift)? | |
> | ASD or LRFD: each combination's basis against each template's method cell (the method cell is always bound, never left at its default) | |
> | Units: STAAD's printed units against the template's (kip, kip-in, in, ksi) | |
> | Do STAAD member ids stay stable across revisions, or do the connection keys need marks from the model's source? | |
> | Excel on the production machine: version (365 functions?), whether data tables recalculate on demand, sheet passwords, which input cells are unlocked | |
> | Who revises a template, and does a re-pinned template hash rerun the calcs that used it? (proposed: yes) | |
> | Forces at the joint against the bolt line or work point: eccentricity, offsets, the gusset geometry STAAD does not hold | |
> | Is a group's envelope calc acceptable as the calc of record for every connection in the group? | |
> | Where section properties come from (the published AISC shapes database?) | |

> [!info]- What came from pipe-review, and what did not
> pipe-review is the support review tool that does this job for pipe supports (CAESAR → support-local design loads → Mathcad calcs → status). Its generic machinery is copied into `scripts/calcflow/` at its commit `d4b33aa`, with a provenance line in each file.
>
> **Copied:**
> - `cs/Xlsx.cs`: the `.xlsx` reader with tables and defined names, and the writer, both without Excel.
> - `cs/Win32.cs`.
> - `cs/State.cs`: CSV, natural sort, calc run state.
> - `Import-CalcFlow.ps1` (was `Engine.ps1`), `Stage.ps1`, `ExcelCom.ps1`, `stages/Convert-Workbook.ps1`, `stages/Probe-Com.ps1`.
> - `tests/Assert.ps1` and `tests/Run-Tests.ps1`, and the Xlsx, State and Lint tests.
>
> The namespace became `CalcFlow` (was `PipeReview`), the prefix `Cf` (was `Pr`), and the Mathcad parts were dropped.
>
> **Not copied:** the CAESAR parser, the support register's schema, the engine's rules, reports, migration, Mathcad COM, the project layout, docs and CI. Those are pipe-specific; CalcFlow writes its own.
>
> **Note for later:** `cs/Xlsx.cs`'s writer overlaps Pluto's `XlsxBook` (`scripts/lib/writers/XlsxWriter.cs`), and is to be merged.
