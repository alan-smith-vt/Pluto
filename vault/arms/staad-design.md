---
title: STAAD design in the frame workbook
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# STAAD design in the frame workbook

A model's STAAD design and output requests are in the frame workbook itself
([[vault/arms/frame-workbook|frame-workbook]]): what STAAD does after the analysis.
- **Each member's parameters are on its own Frames row**, in a block of columns at the right (K factors, unbraced
  lengths, `DFF` …). The row is the member, so a value follows its member through a rename, a sort or a deletion.
- **Three STAAD sheets** hold what is not per member: the parameter sets, the rows by list, and the run.

SAP takes none of it: `-S2k` leaves it out. Code: `StaadDesign` (`scripts/arms/frame/FrameDesign.cs`), the sheets in
`FrameWorkbook.cs`. Checked in the session on the round 4 probe and a SAP rack; the production machine's run is still
to do, so this note stays `draft`.

It replaces the separate design workbook (`<model>.design.xlsx`) of 2026-11-24, which is no longer read. `-Std` warns
when one lies beside the workbook.

## Run

```powershell
# a .std (STAAD's model): the workbook, its design read from the .std's commands after the analysis
... scripts\frame\Build-Frame.ps1 -In "<project dir>\frame.std" -Xlsx -Out "<project dir>\frame"
# the .std from the workbook, design and all
... scripts\frame\Build-Frame.ps1 -In "<project dir>\frame.xlsx" -Std
# a model with no design (one from SAP): the workbook with a starter
... scripts\frame\Build-Frame.ps1 -In "<project dir>\rack.s2k" -Xlsx -NewDesign
```

`...` stands for `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`.

**A starter (`-NewDesign`)**, with `-Xlsx` or `-Std`, for a model with no design:
- set 1 (AISC UNIFIED 2016, LRFD) in the model's units;
- a run of Pluto's own prints and `CHECK CODE ALL`.

Then type each frame's parameters on its Frames row. A model that has a design keeps it. Without a starter, two rows
do the same: a set on STAAD sets, and a `CHECK CODE` row on STAAD run.

**With no design at all,** `-Std` writes Pluto's own prints.

> [!info]- Where each thing goes
> | Where | Holds |
> |---|---|
> | Frames, the STAAD block | one column per parameter: FYLD FU KY KZ LY LZ UNB UNT CB DFF DJ1 DJ2 NSF RATIO MAIN TRACK BEAM, then Other (`STP 1; TORSION 1`). This frame's value, in the first set's units |
> | Frames, Groups | the SAP groups the frame is in, to filter by (read-only, as written) |
> | Frames, Length | the member's length, live |
> | STAAD sets | one row per parameter set, STAAD's `PARAMETER n`: Set, Code (`AISC UNIFIED 2016` …), Method (LRFD / ASD), Units (the UNIT its values are in) |
> | STAAD parameters | rows by list: a set's values for ALL, values for links, and every row of a set after the first |
> | STAAD run | in order: Load list, Action (CHECK CODE, SELECT, PRINT, COMMAND), Set, Members, Units, Text |
> | Settings | STAAD units (the UNIT prints and commands are in; blank: the model's), STAAD analysis print (`PERFORM ANALYSIS PRINT …`) |
>
> **The Frames block belongs to the first set** on STAAD sets. The block is a column group: the `+` / `-` above the
> columns folds it away. It is written open when the model has a design.
>
> **A blank cell** takes the set's value for ALL (its row on STAAD parameters), else STAAD's default. For LY, LZ, UNB
> and UNT STAAD's default is the member's own length: the Length column shows it.
>
> **To give a value to many frames:** filter the Frames sheet (by Groups, Section, Kind or Line), type the value in
> the first cell, and fill down.
>
> **One place for each value.** A row on STAAD parameters that names frames of the first set is refused: their values
> go on their Frames rows.
>
> **DJ1 and DJ2** name joints by label, as on Joints. `-Std` writes their STAAD numbers.

> [!info]- Members and load lists on the STAAD sheets
> **Members** name the model as the workbook does:
> - ALL;
> - a group, as on the Groups sheet (`COLUMNS`, or STAAD's `_COLUMNS`);
> - frame and link labels;
> - ranges `1 TO 4` of number labels;
> - or a mix.
>
> `-Std` writes them as STAAD's group names and member numbers. A SAP label like `B1a` becomes its STAAD number.
>
> **Load lists:**
> - ALL;
> - STAAD load numbers and ranges (the STAAD loads sheet);
> - ENV and envelope numbers.
>
> A blank load list keeps the row above's.

> [!info]- What -Std writes
> After `PERFORM ANALYSIS` (with the analysis print option) and `DEFINE ENVELOPE`:
> - each STAAD run row in turn, its UNIT (when it changes) and then its LOAD LIST;
> - a set's PARAMETER block before the first CHECK CODE or SELECT that uses it, and again when another set came
>   between.
>
> **A set's block:** `PARAMETER n`, CODE, METHOD, its rows by list in order (ALL first), then, for the first set, the
> frames' own values. Those are one line per parameter and value, with the members that share it:
> `KY 1.2 MEMB 1 TO 4`.
>
> **Checks: exact or refused.** Every name is checked against the model, and each one missing is refused by its row;
> nothing is written. All the problems are named at once:
> - a member, a group with no member, or a range with no member;
> - a load or envelope number the `.std` does not write (a range must hold one at least);
> - a set a run row names that STAAD sets does not have;
> - a DJ1 or DJ2 joint the model does not have;
> - design values on Frames with no set on STAAD sets.
>
> Reading the workbook refuses:
> - a parameter cell that is not a number;
> - an unknown action;
> - a PRINT or COMMAND with no text.
>
> **A notice, not a refusal:** design values on Frames with no CHECK CODE or SELECT under the first set are not
> written.

> [!info]- Read from a .std
> STAAD's commands after the analysis become the design. The rule for each kind of command:
> - **`PARAMETER n`** starts a set. Its CODE and METHOD go on STAAD sets, with the UNIT in force.
> - **Parameter lines of the first set** (`NAME value ALL | MEMB <list>`) are read as STAAD reads them, a later line
>   of a parameter winning:
>   - a value for ALL is the set's ALL row, and clears the lists before it;
>   - a value for a list goes to each frame it names, on its Frames row. `KY 1.2 MEMB _COLUMNS` fills every column's
>     row;
>   - a list's links (STAAD's truss members) get a row by list.
> - **Parameter lines of a later set** stay rows by list, their members in the model's labels.
> - **`CHECK CODE` and `SELECT`** become run rows with the LOAD LIST and the set in force.
> - **`PRINT …`** becomes a run row.
> - **Anything else** becomes a COMMAND row, as written.
>
> **Notices.** Two things in a `.std` read the same in the design, with a notice:
> - a parameter under another UNIT than its set's first;
> - a parameter given after a check of its set (it applies to every check of the set).
>
> **The read-back check** compares the design as STAAD reads it: each set's code and method, each parameter's value
> for ALL and for each member, and the run's steps in order. So `KY 1.2 MEMB _COLUMNS` and `KY 1.2 MEMB 1 TO 4` are the
> same.

> [!info]- Checked in the session
> - **The round 4 probe** goes `.std` → workbook → `-Std` identical as STAAD reads it. Its `DFF 240 MEMB 5 6 7 11` is
>   on those four Frames rows, and `FYLD 50 ALL` is the set's ALL row.
> - **Values typed on Frames rows** reach the `.std`, one line per parameter and value:
>   - `KY`, `LY` on four columns: `KY 1.2 MEMB 1 TO 4`;
>   - `DJ1`, `DJ2` by joint label;
>   - Other: `STP 1 MEMB 8 9`, `TORSION 1 MEMB 8`.
> - **A renamed frame keeps its values:** frame 5 renamed `B5` is STAAD member 12, with its `UNB`, `DFF`, `DJ1`.
> - **A deleted frame** takes its values with it.
> - **A second set** by list, with its own check, is written after the first.
> - **Refused by cell:** text in a number column; a row by list naming the first set's frames.
> - **The edited `.std` reads back** to the same values on the same rows.
> - **A SAP rack with `-NewDesign`:** the starter is on its sheets, and `KY` / `UNB` typed on three rows come out as
>   `MEMB 1 TO 3`, the STAAD numbers of `C1`, `C2`, `B1a`.
> - **SAP's file with `-Merge`** gets the design back: the sets, rows by list and run as they are, and the frames'
>   values by label.
> - **Through the runner under pwsh 7:** `-In probe.std -Xlsx -Std`, `-In round4.xlsx -Std`, `-S2k` (no Pluto table in
>   it), `-In <SAP export> -Merge round4.xlsx -Std`.

> [!info]- Split frames, and limits
> - **Each Frames row is one STAAD member.** A workbook is written split where SAP's automatic mesh joins a frame to
>   joints on its span ([[vault/arms/frame-workbook|frame-workbook]]). The pieces keep the frame's design values.
> - **A joint added on a frame's span afterwards** is split at `-Std`, with a notice. The pieces take the frame's
>   values, but a blank LY, LZ, UNB or UNT is then each piece's own length in STAAD.
>   `-In <workbook> -Xlsx -Out <new>` gives the pieces rows of their own.
> - **STAAD only.** SAP's design (its overwrites and preferences) stays in the frame workbook's hidden base, carried as
>   it is.
> - **Iterative design** (`SELECT` followed by `PERFORM ANALYSIS` again) is written as COMMAND rows, in order, not
>   checked.
> - **A PRINT or COMMAND row's text** is written as typed: member and joint numbers in it are STAAD's, not labels.
> - **An older workbook** with a "STAAD commands" sheet is read into the design, with a warning; `-Xlsx` then writes
>   it in this layout.
