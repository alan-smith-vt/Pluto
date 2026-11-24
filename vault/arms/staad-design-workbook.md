---
title: STAAD design workbook (<model>.design.xlsx)
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# STAAD design workbook

A model's STAAD design and output requests sit in a workbook of their own, `<model>.design.xlsx`, beside the frame
workbook. The frame workbook ([[vault/arms/frame-workbook|frame-workbook]]) holds the structure and loads; this one holds
what STAAD does after the analysis:
- its design parameters, in a spreadsheet by group or member list;
- its code checks and member selection;
- its prints.

SAP takes none of it. Code: `StaadDesign` (`scripts/arms/frame/FrameDesign.cs`) and `StaadDesignWorkbook`
(`FrameDesignWorkbook.cs`). Checked in the session on the round 4 probe; the production machine's run and STAAD's are
still to do, so this note stays `draft`.

## Run

```powershell
# a .std (STAAD's model): both workbooks, the design one from the .std's commands after the analysis
... scripts\frame\Build-Frame.ps1 -In "<project dir>\frame.std" -Xlsx -Out "<project dir>\frame"
# the .std from the two workbooks: the design workbook beside the model workbook is found by its name
... scripts\frame\Build-Frame.ps1 -In "<project dir>\frame.xlsx" -Std
# another design workbook for the same model
... scripts\frame\Build-Frame.ps1 -In "<project dir>\frame.xlsx" -Std -Design "<project dir>\frame.strength.design.xlsx"
```

`...` stands for `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`.

**How `-Std` picks the design:**
- **`-Design <file>`** names it.
- **Otherwise** `<model>.design.xlsx` beside the model workbook. With `-In <SAP export> -Merge <master>`, it is the one
  beside the master.
- **With none**, `-Std` writes the model's carried commands (a `.std` read straight to `-Std`, or an older workbook), else
  Pluto's own prints.

**When `-In <x>.std -Xlsx` writes it:** the design workbook takes the `.std`'s commands after the analysis and the model
workbook keeps none, so each item has one place to edit. An existing design workbook is kept as `.bak`. An older frame
workbook with a "STAAD commands" sheet moves it to a design workbook the same way on its next `-Xlsx`.

> [!info]- The sheets
> | Sheet | Holds |
> |---|---|
> | Start | what it is, how it runs, the sheets |
> | Settings | Units (the UNIT prints and commands are in; blank: the model's), Analysis print (`PERFORM ANALYSIS PRINT …`), Model (a record) |
> | Codes | one row per parameter set, STAAD's `PARAMETER n`: Set, Code (`AISC UNIFIED 2016` …), Method (LRFD / ASD), Units (the UNIT its values are in) |
> | Parameters | one row per set and members: Set, Members, then a column per parameter (FYLD FU KY KZ LY LZ UNB UNT CB DFF DJ1 DJ2 NSF RATIO MAIN TRACK BEAM), and Other (`STP 1; TORSION 1`). A blank cell writes nothing |
> | Run | in order: Load list, Action (CHECK CODE, SELECT, PRINT, COMMAND), Set, Members, Units, Text |
>
> **Members** name the model as the frame workbook does:
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
> - STAAD load numbers and ranges (the model workbook's STAAD loads sheet);
> - ENV and envelope numbers.
>
> A blank load list keeps the row above's.
>
> **The order `-Std` writes:**
> - Each Run row in turn, its UNIT (when it changes) and then its LOAD LIST.
> - A set's PARAMETER block (UNIT, `PARAMETER n`, CODE, METHOD, a line per filled parameter cell) goes before the first
>   CHECK CODE or SELECT that uses it, and again when another set came between.

> [!info]- Checks: exact or refused
> On `-Std`, every name in the design workbook is checked against the model, and each one missing is refused by its
> row; nothing is written. All the problems are named at once:
> - a member, a group with no member, or a range with no member;
> - a load or envelope number the `.std` does not write (a range must hold one at least);
> - a set Run names that Codes does not have.
>
> Reading the workbook refuses:
> - a parameter cell that is not a number;
> - an unknown action;
> - a PRINT or COMMAND with no text.

> [!info]- Read from a .std
> STAAD's commands after the analysis become the sheets. The rule for each kind of command:
> - **`PARAMETER n`** starts a set. Its CODE and METHOD go on Codes, with the UNIT in force.
> - **Parameter lines** (`NAME value ALL | MEMB <list>`) go on Parameters, one row per set and member list. A
>   parameter without a column goes in Other.
> - **`CHECK CODE` and `SELECT`** become Run rows with the LOAD LIST and the set in force.
> - **`PRINT …`** becomes a Run row.
> - **Anything else** becomes a COMMAND row, as written.
>
> **Notices.** Two things in a `.std` read the same in the design workbook, with a notice:
> - a parameter under another UNIT than its set's first;
> - a parameter given after a check of its set (it applies to every check of the set).
>
> **Checked in the session.** The round 4 probe, and the earlier STAAD-style files, go `.std` → design workbook →
> `-Std` identical as STAAD reads them. Member lists are compared by the members they name: `5 TO 7 11` is `5 6 7 11`.
> Edits made in Excel reach the `.std`:
> - `KY`, `LY` for the group `COLUMNS` (written as STAAD's `_COLUMNS`);
> - a group mixed with a member label;
> - a SAP label written as its STAAD number.
>
> Names the model does not have are refused by row. Through the runner under pwsh 7:
> - `-In probe.std -Xlsx -Std` writes both workbooks;
> - `-In round4.xlsx -Std` gives the same `.std`;
> - SAP's file with `-Merge` gives it too.

> [!info]- Limits
> - **A frame split** at `-Xlsx` (SAP's automatic mesh, [[vault/arms/frame-workbook|frame-workbook]]) leaves its pieces
>   labelled `<label>_k`. A member list naming the old label is refused; a group follows the split.
> - **STAAD only.** SAP's design (its overwrites and preferences) stays in the frame workbook's hidden base, carried as
>   it is.
> - **Iterative design** (`SELECT` followed by `PERFORM ANALYSIS` again) is written as COMMAND rows, in order, not
>   checked.
