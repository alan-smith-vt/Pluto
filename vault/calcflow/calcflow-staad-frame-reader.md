---
title: STAAD .anl frame reader → CalcFlow's analysis package
status: draft
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# STAAD .anl frame reader

CalcFlow's step 1 ([[vault/calcflow/calcflow-overview|calcflow-overview]]): a STAAD.Pro output file (`.anl`) of a **frame model** becomes the **analysis package**, one folder of CSV files that every connection calc takes its inputs from. The package holds:
- joints and members;
- sections, BETA, releases, offsets and truss members;
- supports;
- load cases with their combination factors;
- the member end forces of every printed case;
- a joint index of which member ends meet where.

The code:
- `scripts/lib/readers/AnlFrameReader.cs` (`AnlFrameReader.Read`, `StaadAxes`);
- `scripts/lib/exporters/FramePackageWriter.cs`;
- the runner, `scripts/calcflow/Export-AnlFrame.ps1`.

The reader is part of Pluto's one C# batch (`scripts/lib/Config.ps1`). Built and tested 2026-11-06 on a made-up fixture; **not yet run on a real model**.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\calcflow\Export-AnlFrame.ps1 -Anl "<project dir>\staad\MODEL-A\R03\MODEL-A.anl" -Out "<project dir>\calcflow\package\MODEL-A\R03"
```

**Before you run it:**
- The model must print its end forces: put `PRINT MEMBER FORCES ALL` after the analysis. Pluto's `FrameStd` writes it.
- The exit code is 0 when the package is written, 1 when the model has errors (each is listed and nothing is written), and 2 for bad arguments.
- A package is project data, so a folder inside the repo is refused.

**Since 2026-11-24 the reader lives on branch `pipestress`** (`scripts/lib/`); `CalcFlow` merges it. Added that day:
- STAAD 2025's database form of a section (`TABLE 'W' ST 'W24X55'`) and double angles (`TABLE LD` / `SD`);
- comment lines in the echo are skipped;
- `LOAD LIST` is parsed, and every output command is recorded with the list it ran under (`prints.csv`); one whose table is not read is named in a warning (`PRINT_NOT_READ`);
- a GLOBAL force print, a force-table line that is not a row, and a member with forces at one end only are errors.

**Fixed 2026-11-25: `PRINT MEMBER FORCES LIST ...`.** STAAD echoes the input line after its line number, and a list puts numbers on it. With three or more, the echoed line was taken for a row of the table and the whole output was refused: `FORCE_MEMBER_UNKNOWN` for member 0 (or the line number), or `FORCE_ROW_UNREAD`, though every real row had been read. Echoed lines (a number with a period, then text) are now skipped in the force table, a continued list too. A row of numbers before a table's first member row is reported with its text (`FORCE_ROW_UNREAD`) instead of being given to member 0. The other tables (reactions, displacements, section displacements, member information and properties, code checks) were tried with lists of 3 to 9 numbers on the probe and read them as before.

**The probe ran on 2026-11-24** (STAAD.Pro 2025; `scripts/lib/probes/anl-prints-probe.anl`, the reader's first real file). The reader took it as it is: 18 joints, 32 members, 14 cases, all 384 end-force rows, no errors. Written from it the same day: **support reactions** and **joint displacements** (`reactions.csv`, `displacements.csv`; units from each table's title line; a row whose numbers run together is an error).

**Every usual output command is read since 2026-11-24** (`scripts/lib/readers/AnlFramePrints.cs`, written from the probe's `.anl`). On that file: 112 code check rows from eight `CHECK CODE` commands, 654 detail rows, 32 members of information and of properties, 160 section displacement blocks, and nothing noted but the member offset.

> [!info]- The printed tables besides the forces (code check, member tables, section displacements)
> | Command | Read as | Checked |
> |---|---|---|
> | `CHECK CODE` | one row per member per command: the code as STAAD titles it, profile, PASS / FAIL, ratio, governing clause, case and location, the forces of that case with their units. Where TRACK 1 or 2 printed them, the checks behind the result (demand, capacity, ratio, clause, case) as detail rows. | a result in a layout that is not known is an **error** (`CODE_CHECK_UNREAD`); members the command names with no result are a warning |
> | `PRINT MEMBER INFORMATION` | start and end joint, length (the offsets taken off), BETA, releases as printed | joints that differ from the echo are an **error**; BETA and releases that differ are warnings |
> | `PRINT MEMBER PROPERTIES` | AX IZ IY IX AY AZ SZ SY, in the unit of the title line | a member whose rows are not eight numbers is left out, with a warning |
> | `PRINT SECTION DISPLACEMENTS` | global X Y Z at the 13 stations of each member and case, and STAAD's `MAX LOCAL DISP` line per member (displacement, where, case, L / displacement) | a block that is not 13 stations of three numbers is an **error** |
> | `PRINT JOINT COORDINATES` | nothing new: checked against the echo | a coordinate that differs is an **error** |
>
> - **Three layouts of a code check result**, by code and TRACK:
>   - AISC 360-16 and 360-22: a box per member (`Member No`, `Status`, `Ratio`, `Loadcase`, `Location`, `Ref`), with all six forces under the code's axis names (Pz Vy Vx Tz My Mx = FX FY FZ MX MY MZ);
>   - AISC 360-05 and 360-10, and the older codes at TRACK 0 and 1: a table (`MEMBER TABLE RESULT/ CRITICAL COND/ RATIO/ LOADING/`) with FX, MY and MZ;
>   - `CODE AISC` (ASD 9th) and `CODE LRFD` (LRFD 3rd) at TRACK 2: a drawing of the member with a `DESIGN SUMMARY` in its own units.
> - FX is printed without its sign, with C or T beside it.
> - A member STAAD does not design (a prismatic section) is a row NOT DESIGNED with STAAD's message, in each of its three wordings.
> - A deflection check (DFF) is a `CHECK CODE` like any other: its row carries the load list it ran under.
> - **Output headings that read like commands.** The indexer starts a section at `JOINT COORDINATES` over a printed table and at `UNITS ARE - INCH` over the section displacements. The frame reader gives such a section back to the command that printed it: only a line with STAAD's echo number starts a command.
> - A code other than these (another country's, a later edition with another layout) is named as not read, or refused where a result would be lost: run the probe with that code as one more block (`scripts/lib/probes/README.md`).


> [!info]- What is read, and from where
> **The input echo.** STAAD writes every input line back into the `.anl` as `   NN. <text>`.
> - Only those lines are read. Page headers, notes and the analysis output are not.
> - A line ending in ` -` continues on the next, and `;` splits commands.
> - Lists take ids, `a TO b [BY s]`, `ALL` and `_GROUP` names (from `START GROUP DEFINITION`).
>
> | Command | Read as |
> |---|---|
> | `UNIT` | the units of each command after it (`AnlUnitStamper`). Geometry is stored in **inches**. |
> | `JOINT COORDINATES` | id, x, y, z. `REPEAT`, generation and `CYLINDRICAL` are **errors**. |
> | `MEMBER INCIDENCES` | id, start joint (A), end joint (B). Generation is an error. |
> | `MEMBER PROPERTY <country>` | the spec as written (`TABLE ST W14X90`, `PRIS …`, `UPTABLE n name`). Also the section name and a family: W, S, M, HP, WT, C, MC, L, 2L, HSS, PIPE or PRIS. |
> | `CONSTANTS` → `BETA` | numeric BETA, with member lists or `ALL`. `ANGLE`, `RANGLE`, `REFERENCE` and `REFJT` are kept, with a warning that the beta is unknown, and the axes are then taken at BETA 0. |
> | `MEMBER RELEASE` | `START / END / BOTH` with FX…MZ. Partial (`MP f`) and spring (`KMZ f`) terms are kept as written, with a warning. |
> | `MEMBER OFFSET` | `START / END` x y z in inches, and `LOCAL`. A warning says the end forces act at the offset point. |
> | `MEMBER TRUSS / TENSION / COMPRESSION / CABLE` | the member's Kind |
> | `SUPPORTS` | `FIXED`, `PINNED`, `FIXED BUT <dofs> [K… f]`, `ENFORCED [BUT …]`: the restrained DOFs. A spring is not a restraint. `INCLINED` and other kinds are kept as written, with a warning. |
> | `LOAD n [LOADTYPE t] [TITLE s]` | a primary case |
> | `REPEAT LOAD` | the current case becomes `REPEAT`, with its factors |
> | `LOAD COMBINATION [SRSS\|ABS] n title` | a combination, with its factor lines (`1 1.2 2 1.6`) |
> | `PERFORM ANALYSIS`, `PDELTA …` | the analysis, as written |
> | `SET Z UP` | **error**: Y up only for now. Pluto's `FrameStd` rotates to Y up for the same reason. |
> | `SET ECHO OFF` | **error**: the input after it is not in the file |
>
> **End forces** come from every `PRINT MEMBER FORCES` table. The fast byte parser is the existing `AnlForceParser`.
> - **Units:** those of the table's own `ALL UNITS ARE -- KIP  FEET` line. A later table in other units is converted to the first one's, with a warning. A table without that line is an error.
> - **Values:** **kept as STAAD printed them**: member local axes, and STAAD's sign (the force on the member at its end).
> - **Ends:** each row is placed at end A or B by its joint. A row for an unknown member, or at a joint that is neither end, is an error.
> - **Duplicates:** a member end and case printed twice keeps the later print, with a warning.
> - **Combinations:** taken as printed, never superposed by Pluto.

> [!info]- STAAD's member axes (Y up)
> - **x** runs from the start joint (A) to the end joint (B).
> - **A non-vertical member at BETA 0:** z = x × Y (horizontal), and y = z × x (pointing up).
> - **A vertical member** (its horizontal projection below 1e-6): z = +Z, and y = z × x.
>   - pointing up: y = −X;
>   - pointing down: y = +X.
> - **BETA** then turns y and z about x, right-handed (`StaadAxes.Compute`).
>
> This is the rule `FrameStd` writes by ([[vault/format/frame-to-staad|frame-to-staad]]). Its BETA sign is still marked unconfirmed there, until STAAD has run the probe.
>
> A member within about 0.6° of vertical but not vertical gets a warning: STAAD's default axes swing between the two rules there.

> [!info]- The package (`-Out` folder)
> | File | Columns |
> |---|---|
> | `manifest.json` | schema `calcflow-analysis-package/1`, the source file name and SHA-256, geometry units (in), force and moment units, analysis, property country, counts, every problem |
> | `nodes.csv` | Node, X (in), Y (in), Z (in) |
> | `members.csv` | Member, NodeA, NodeB, Section, Family, Property, Beta, BetaKnown, ReleaseA, ReleaseB, OffsetA (in), OffsetB (in), Kind, Length (in), local x / y / z (`xX…zZ`, unit vectors in global axes) |
> | `supports.csv` | Node, Type, Restrained, Springs, Spec |
> | `loadcases.csv` | LC, Kind (PRIMARY / REPEAT / COMBINATION / PRINTED), Method (SRSS / ABS), LoadType, Title, Factors (`1*1.2 + 2*1.6`), HasForces |
> | `endforces.csv` | Member, End (A / B), Node, LC, `Fx (kip)` … `Mz (kip-ft)`: member local axes, STAAD's sign, the units in the header |
> | `ends.csv` | the joint index: Node, Member, End, Role, Section, Family, Kind, Released, Dir (unit vector away from the joint), local y and z, ThroughMember / ThroughEnd |
> | `joints.csv` | Node, X / Y / Z (in), Ends, Columns, Beams, Braces, Support |
> | `prints.csv` | every output command in file order: Order, Command, LoadList, Cases, Read, Rows |
> | `reactions.csv`, `displacements.csv` | Node, LC and six components, as printed (global axes), the units in the header |
> | `codechecks.csv` | Print (the command's Order), Member, Code, Profile, Table, Status, Ratio, Critical, LC, Location, Axial, FX to MZ, Force unit, Length unit, Starred, Note |
> | `codecheckdetails.csv` | Print, Member, Group, Check, Demand, Capacity, Ratio, Reference, LC, Location |
> | `memberinfo.csv`, `memberproperties.csv` | the two printed member tables, the unit in the header |
> | `sectiondisplacements.csv`, `sectionmaxima.csv` | Member, LC, Station, X Y Z; and per member the largest local displacement, where, the case and L / displacement |
>
> **The joint index:**
> - **Role:** Column (within 10° of vertical), Beam (within 10° of horizontal), else Brace.
> - **Through member:** the collinear end (within 5°) of the same section at the joint, i.e. a continuous member. Typing rules (Phase 5) will build on it.
>
> **Format:** CSVs are UTF-8 with a BOM, CRLF and invariant culture; `manifest.json` has no BOM. A model with errors writes nothing.

> [!info]- The shared reader got two fixes (they also serve the plate / tank workflow)
> **1. A section now ends at the next command** (`AnlIndexer`).
> - Before, a section ran to the next marker on a fixed list. A `PRINT MEMBER FORCES` table followed by `PRINT SUPPORT REACTION` and a `CHECK CODE` run therefore read their numeric rows as member forces.
> - On the fixture, the old indexer gives the force parser 16 rows instead of 10 for the second print. Three of the six extra rows land at joints that are not ends of their member. The other three read silently as false forces at member 2's own joints.
> - The fix adds **echo-only markers**, matched only on STAAD's command echo and as whole words:
>   - `LOAD LIST` and `REPEAT LOAD`, ahead of `LOAD`;
>   - after the existing markers: `PRINT SUPPORT REACTION`, generic `PRINT`, `PARAMETER(S)`, `CHECK CODE`, `SELECT`, `SET`, `DEFINE`, `PDELTA`, `PERFORM`, `STEEL`, `CODE`, `MEMBER TRUSS / TENSION / COMPRESSION / CABLE`, `SPRING`, `INACTIVE`, `CHANGE`, `CUT OFF`, `START`, `END`.
> - The existing markers are matched as before. `StaadModel`'s index cache is now `.anl.index.v2.json`, so an index built with the old markers is not reused.
>
> **2. A frame-only file opens.** `StaadModel` no longer crashes when there is no `ELEMENT INCIDENCES` (the element list is empty).

> [!info]- Verification (2026-11-06, Linux)
> - **Strict compile.** Roslyn, C# 5 (`-langversion:5`), warnings as errors, against the .NET Framework 4.8 reference assemblies: the whole `scripts/lib` + `scripts/arms` batch, and `scripts/calcflow/cs` with it. 0 errors, 0 warnings.
>   - The batch compiled the same way before the change.
>   - mono's `mcs` cannot compile `SectionCuts.cs`'s COM indexed properties, so Roslyn was used.
> - **Tests:** `scripts/calcflow/tests/Run-Tests.ps1` under PowerShell 7 with the batch prebuilt (`PLUTO_DLL`) gives 29 tests, 0 failed.
>   - **AnlFrame (14 tests), on `tests/fixtures/frame-demo.anl`:**
>     - counts and units;
>     - inches, sections and families;
>     - the axes of six members: BETA 90 up, BETA 0 up, down, +X, −Z, inclined;
>     - releases, offset, truss, supports;
>     - the five load cases with their factors;
>     - eight end forces exactly, including the kip-in print converted;
>     - no rows from the reaction or code check prints;
>     - the joint index and a through member;
>     - CRLF;
>     - errors for `SET Z UP`, `REPEAT`, no force print and an unknown group;
>     - `BETA ANGLE`;
>     - every package file;
>     - the runner's exit codes 0 / 1 / 2.
>   - Plus Lint, State and Xlsx.
> - **Skipped under PowerShell 7: the `StaadModel` test.** It needs the .NET Framework JSON serializer. The same check was run under mono instead: 0 elements, 7 members, 80 force rows. Before the fix it threw `NullReferenceException` in `ParseElementSection`.
> - **Not run here:**
>   - Windows PowerShell 5.1 (`powershell -File scripts\calcflow\tests\Run-Tests.ps1` on the production machine);
>   - a real STAAD `.anl`. **First on the production machine:** export one real model and compare five end forces against STAAD's output.
