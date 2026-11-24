---
title: CalcFlow process review (usability, .anl coverage, the workbook revamp)
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/audits/Audits map|Audits map]]*

# CalcFlow process review

Three independent reviews of CalcFlow as built through step 3 ([[vault/calcflow/CalcFlow map|CalcFlow map]]), made on 2026-11-24 against the engineer's sketch of a workbook that works "sort of like a GUI". The review changed no code. **Everything under "proposed" is a proposal: nothing here is decided or built.**

**Verdict.** The engine is sound: guards, units, sign checks, named gaps, records. The front door is wrong for its user:
- it is a command line plus a configuration language (about 45 named concepts, 8 commands, 21 sheets in 4 workbooks);
- the template shape the engineer calls the most common, a pasted table of beam, node, load case and forces, is the one the engine supports least;
- of the three model outputs the engineer named, only the end forces are read from the `.anl`.

The sketch is the right direction, with two corrections (a link cell instead of a macro button; the model data in a generated workbook) and four things it leaves out that correct results need (sign map, units, load cases in scope, envelope or every case).

## The sketch (the engineer's, in generic names)

- **Start:** a button "post-process .ANL" that runs the script and tabulates everything read from the `.anl` (reactions, enveloped end forces, all end forces).
- **Model Config:** one row per beam: Beam ID, Node I, Node J, Section, Shape, Connection I, Connection J.
- **Connection Lib:** Connection, Template File, Inputs (the top-left cell where a range of input data is pasted), Outputs.
- **Added the same day:** templates also take geometry (weld leg lengths, anchor coordinates, plate dimensions, edge distances). The same template is needed in several variants, and the variant must be chosen per beam node.

> [!info]- Usability findings
> 1. **The pasted load table is the least supported shape.**
>    - A table row can hold only `load:` and `case:` values. A beam or node number per row is refused (`cs/Library.cs` `Bindings.Check`; `cs/Plan.cs` `PerCase`).
>    - Many connections in one copy (`multi_connection`) is refused as not planned (`Plan.cs`, `Step`).
>    - There is no top-left cell: every column is its own input row.
>    - Rows the run does not fill are not cleared (`cs/Run.cs` `CalcJob.Build`), so sample rows left in a template would be calculated.
>    - Harvest sees a table only when it is an Excel table object (`cs/Harvest.cs`).
> 2. **Learning load:** 2 scripts, 8 commands, 16 switches; 4 workbooks with 21 sheets; about 20 CSV files; 7 binding kinds, 4 demand modes, 3 shapes, 7 statuses, 23 problem codes.
> 3. **Where a non-programmer stops:** first at the PowerShell command line; then at harvest's Inputs sheet, where every Binding is blank (212 rows on the two generic templates, of which the example library keeps 64).
> 4. **Words:** "Combos", `LC`, "Governing Combo" and "Governing Case" are one thing (load case). End `A/B` is the engineer's I / J. `Support` is a member number. `Shape` means the template's layout here and the section's shape in the sketch.
>
> **What each concept should become:**
>
> | Concept | Proposed | Why |
> |---|---|---|
> | Label guards | keep, hidden | harvest fills them; without them a rearranged template is filled wrongly, silently |
> | SHA-256 pin | hide | re-pin automatically while every guard holds |
> | Library text export | fold into Run | the diffable text stays; the step goes |
> | Sign maps | keep visible | six drop-downs per template and a "checked" flag; a wrong sign cannot be detected later |
> | Slots / Support | merge | "frames into" per beam end, guessed first |
> | Params and overrides | merge | a schedule row per connection mark (below) |
> | 7 binding kinds | merge | one drop-down of plain field names |
> | Steps | hide | one template per mark until a second is added |
> | Demand modes | two choices | all load cases, or envelope (max and min with concurrent forces) |
> | Plan / run split | merge | Run checks first and stops on errors; "check only" stays |
> | Harvest to library copy | merge | harvest writes straight into the workbook |
> | Row per member end | merge | edit one row per beam; the per-end table is the results |

> [!info]- .anl coverage of the engineer's usual print commands
> The commands: `CHECK CODE ALL`, `PRINT JOINT COORDINATES`, `PRINT MEMBER INFORMATION`, `PRINT MEMBER PROPERTIES`, `UNIT INCH KIP`, a strength `LOAD LIST`, `PRINT SUPPORT REACTIONS`, `PRINT MEMBER FORCES`, a service `LOAD LIST`, `PRINT JOINT DISPLACEMENTS`, `PRINT SECTION DISPLACEMENTS`.
>
> On a fixture with these commands in this order, `AnlFrameReader` returned the model and the end forces only. The other tables gave no rows and no warning (reproduced twice).
>
> | Item | Read today | Gap | Size |
> |---|---|---|---|
> | Joint coordinates | the input echo | the printed table is unused (it would cross-check, and lift the refusal of generated joints) | S |
> | Incidences, sections, BETA, releases | the input echo | the MEMBER INFORMATION table is unread (STAAD's own length, BETA, releases) | M |
> | MEMBER PROPERTIES table | no | AX to IZ and the resolved profile per member; it gives no d, bf, tf, tw | M |
> | Supports | yes | | |
> | **Support reactions** | **no parser anywhere** | per joint per case; `AnlDispParser`'s row logic fits the table | S |
> | **Member end forces** | yes | no completeness check: a row that does not parse is dropped silently; a GLOBAL print passes as local; values go through float32 | S |
> | **Enveloped end forces** | not printed by these commands | computed only in the planner, per registered connection; none per member end in the package | S-M |
> | CHECK CODE results | no | ratio, clause, critical case, location per member; layout varies with code edition and TRACK | M-L |
> | Joint displacements | the plate reader only (`AnlDispParser`) | not wired into the frame reader or the package | S |
> | Section displacements | no | max deflection, location, case, L/DISP per member; its `UNIT =` header line is taken as a UNIT command and stamps later sections cm | M |
> | Load cases, combinations, REPEAT LOAD | yes | a `*` comment before a factor line leaves the combination empty, no warning | S |
> | **LOAD LIST** | indexed, never parsed | nothing records which cases each print covers; a defined but unprinted case gets `HasForces = N` with no warning | S |
> | UNIT changes | yes for geometry and forces | reaction, displacement and code-check unit headers are unread | S |

> [!info]- Reader hazards found by experiment (current build, scratch copies of the fixture)
> - **STAAD 2025's database form** `TABLE 'W' ST 'W24X55'` reads as section `ST` with no family; `TABLE LD ...` (a double angle) reads as family `L`. No warning. pipestress's `StdToFrame.TableOf` reads both forms: reuse it.
> - **`*` comment lines** in the echo: inside MEMBER PROPERTY the package is refused; inside JOINT COORDINATES the joints are truncated. Whether STAAD echoes comments with a line number needs a real file. `AnlGroupParser.Commands` already keeps comments with the next command.
> - **Touching positive numbers** in a force row: the row is dropped silently. Column overflow (`*****`) gives a misleading error.
> - **Duplicated logic** since the merge: three id-list expanders, two LOAD header parsers, three unit-word tables. `StaadInput.Read` (the `.std` reader) is a cleaner source than the echo for joints, members and loads; it lacks REPEAT LOAD and LOAD LIST.
> - **Section dimensions** (d, bf, tf, tw): no STAAD print gives them. `FrameSapLibrary` reads them by shape name from SAP's library.

> [!info]- Scale (a synthetic 5,000 members x 120 cases file, 102 MB)
> - 1,200,000 end-force rows: read in 6 s at 454 MB; written in 5 s more.
> - `endforces.csv` is 71 MB; CalcFlow's load of it took 4 s and 1.1 GB.
> - **One Excel tab holds 1,048,576 rows:** the full table does not fit (104 cases at 5,000 members do). An envelope tab is 120,000 rows.
> - Run time is seconds; memory and the tab limit are the constraints. Everything is held in memory today.

> [!info]- The proposed setup
> **Two files** beside each other:
>
> **The engineer's workbook** (written once by the tool, then saved only by Excel):
> - **Start:** the steps; link cells that run launchers (Post-process .ANL, Check, Run calcs, open Model Data, open Results); settings (the `.anl`, the templates folder, the DCR limit).
> - **Model Config:** Beam ID, Node I, Node J, Section, Shape, Connection I, Connection J, Comment. The two connection cells are drop-downs of the marks.
> - **Connection Lib:** one row per **mark**: Connection, Template, Description. A mark is one template plus one set of geometry values; several marks may share a template.
> - **A schedule tab per template:** one row per mark, one column per geometry input, headed by the template's own labels. Grey rows above carry each column's cell, unit and the template's current value; a blank cell means that value. A new variant is a copied row.
> - **A child tab per fixed table** (anchor coordinates): Connection, No., and the table's own columns; rows only for the anchors that exist. The tool always writes the table's full capacity: the given rows, then the empty-row value, which the library states per column and an engineer confirms.
> - **Templates:** per template, the load block (sheet, paste-at cell, the ordered column fields, max rows), all cases or envelope, where results are read (fixed cells, or a column beside the block), the sign map.
> - **Load Cases:** LC, Title, Kind, Use, Basis.
>
> **`Model_Data.xlsx`** (generated, rebuilt by every post-process run, opened for the user): About, Members, Joints, Supports, Member Ends, Load Cases, Reactions, All End Forces, Enveloped End Forces, Model Changes; Code Check and Displacements once the reader has them. A table past one tab's rows splits into numbered tabs at member boundaries; the CSV stays the full source.
>
> **Underneath:** the tabs are a front end that fills today's `Register` and `Library` objects. A mark is a Type with one Steps row; a schedule column is an optional `param:` input and a Params row per mark. Plan, Run and `XlsxPatch` stay. The eight tables stay as the exported, diffable text.

> [!info]- Decisions proposed
> 1. **The button is a link cell to a `.cmd` launcher** beside the workbook, as the pipe and frame workbooks do (`scripts/arms/workbook/WbRun.cs`, `WbStart.cs`; [[vault/arms/workbook-kit|workbook-kit]]): production machines block macros. The launcher can always be double-clicked when file links are blocked too. An `.xlsm` with a VBA button is refused: the tool cannot author a VBA project without Excel, and it is dead where macros are blocked.
> 2. **The data tabs live in a generated workbook, not in the open one.** Excel holds the open file, so no script can add tabs to it. Rebuilding it in place (closed) drops the user's formatting; COM into the engineer's running Excel is what the stage rules forbid; Power Query needs a part the tool cannot author. This partly supersedes "no single configuration workbook" in [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]]: the library becomes tabs of the project's workbook.
> 3. **Geometry is a schedule row per mark** (not a column per mark, not a long table): 40 marks are 40 rows that filter, sort and fill down. A per-node one-off is another mark, so Model Config shows the variant; parameter overrides leave the UI.
> 4. **The launcher code is copied into CalcFlow** (about 120 lines), not shared: the kit compiles in Pluto's batch and CalcFlow alone, and the kit's writer has no Excel tables.

> [!info]- Build order proposed
> | # | Delivers, usable alone | Biggest risk |
> |---|---|---|
> | 1 | The post-process launcher and `Model_Data.xlsx`; the reader gains reactions, the two load lists, an envelope per member end, the database property form | the `.cmd` link on the production Excel: test it first |
> | 2 | The engineer's workbook, written once (Start, Model Config, Connection Lib, schedules); Check and Run launchers | the tabs and the eight tables drifting apart |
> | 3 | Paste-a-range blocks (beam and node per row, many connections per copy, unused rows cleared) and fixed child tables | sign conventions and stale example rows in real templates |
> | 4 | New beams and DCRs written into Model Config through Excel, the workbook closed | COM writes cannot be tested off the production machine |

> [!info]- What only a real .anl can settle
> No real `.anl` was available: the frame reader has never read one, and every printed table the review appended was written from memory of STAAD's layout. To check on the production machine, on a small generic model run with the usual print commands:
> - the code-check block (code edition, TRACK level);
> - the MEMBER PROPERTIES and MEMBER INFORMATION layouts;
> - the section-displacement layout and its `UNIT =` line;
> - the page-header form, and the column widths at the model's largest ids;
> - whether comments are echoed, and which property form that STAAD version saves;
> - the reaction and displacement unit headers.
>
> A tab is accepted when its row count equals members (or joints) times the listed cases, and five values match STAAD.

## How the review was made

Three agents, each with its own brief and none shown the others' conclusions:
- **Usability** (the design-critique and UX-copy skills): the generated workbooks of the fixture project, the docs, the code.
- **.anl coverage** (the testing-strategy skill): the merged readers, with experiments on scratch copies of the fixture and a synthetic large file.
- **The revamp** (the system-design and architecture skills): the workbook kit, pipe-review's ownership rules, CalcFlow's code; then an addendum on geometry and variants.

Checked a second time by hand: the per-case binding limits, the uncleared table rows, the reader's section switch, the launcher and Start-sheet code of the kit, and two reader runs (the engineer's print order; the database property form).
