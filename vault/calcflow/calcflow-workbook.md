---
title: CalcFlow workbook (Start tab, launchers) and Model_Data.xlsx (post-process)
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow workbook and model data

The front door of CalcFlow is a workbook. `CalcFlow.xlsx` has a **Start** tab whose link cells run the steps; the first step, **Post-process .ANL**, reads the STAAD analysis output and writes **`Model_Data.xlsx`** beside the workbook, with everything read from the model in tabs. Decided in [[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]].

The code is `scripts/calcflow/cs/Workbook.cs` and `ModelData.cs`, run through `CalcFlow.ps1 new` / `post-process` / `launchers`. Built 2026-11-24 and tested on the fixture. **Not yet run on the production machine:** the link cell, the file dialog and Windows PowerShell 5.1 are untested.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 new -Out "<project dir>"
# then open <project dir>\CalcFlow.xlsx and click "Post-process .ANL" on Start (or double-click "Post-process ANL.cmd")
```

> [!info]- The engineer's workbook and its launcher
> - **`new -Out <folder>`** writes `CalcFlow.xlsx` and `Post-process ANL.cmd`, once. An existing workbook is never overwritten: once saved it is the engineer's, and only Excel writes it.
> - **Start** lists the steps. Step 2 is a link cell, `=HYPERLINK("Post-process ANL.cmd", ...)`, relative to the workbook's folder. Excel asks before it opens a launcher; where links to files are blocked, the launcher is double-clicked.
> - **Settings** (`tblStart`): `Anl file`, the `.anl` to read. A name without a folder is looked for beside the workbook. Blank means a file dialog asks for it each time. A step reads the saved file, so save before running one.
> - **The launcher** runs `CalcFlow.ps1 post-process -Workbook "%~dp0CalcFlow.xlsx"` under Windows PowerShell and waits for a key. It finds the workbook beside itself, so the folder can move as a whole. Its path to `CalcFlow.ps1` is the one at write time: `CalcFlow.ps1 launchers -Workbook <CalcFlow.xlsx>` writes it again when the repo moved.
> - Model Config, Connection Lib, the schedule tabs and the Check and Run links are the next increments ([[vault/audits/calcflow-process-review|calcflow-process-review]]).

> [!info]- post-process and Model_Data.xlsx
> `post-process -Workbook <CalcFlow.xlsx> [-Anl <file>] [-NoOpen]`:
> 1. The `.anl` is `-Anl`, else the workbook's `Anl file` setting, else the file dialog.
> 2. The frame reader reads it ([[vault/calcflow/calcflow-staad-frame-reader|calcflow-staad-frame-reader]]). Errors are listed and nothing is written (exit 1).
> 3. The analysis package goes to `<workbook folder>\calcflow\package\<anl name>\`.
> 4. `Model_Data.xlsx` is written beside the workbook and opened. It is generated: every run writes it again. When it is open in Excel, the run writes `Model_Data_2.xlsx` and says so.
>
> | Tab | Rows |
> |---|---|
> | About | the source file, units, counts; **every output command of the file with the load list it ran under, and whether its table is read yet**; the reader's warnings |
> | Members | Member, Node I, Node J, Section, Family, Kind, Length, Beta, releases, offsets |
> | Joints | Node, X, Y, Z, Support, how many columns / beams / braces end there |
> | Supports | Node, Type, Restrained, Springs |
> | Member Ends | one row per member end at a joint: Role, Section, the member it runs through |
> | Load Cases | LC, Kind, Method, Load Type, Title, Factors, Forces printed |
> | All End Forces | Member, End (I / J), Node, LC, Fx to Mz as STAAD printed them, in the member's local axes |
> | Enveloped End Forces | per member end and component: the case of the largest and of the smallest value, with the forces that act with it (12 rows an end) |
> | End Force Extremes | per member end: the largest and smallest of each component on one row. These do not act together |
>
> - **Ends** A / B of the package are shown as I / J.
> - **Size:** a long table continues on numbered tabs past 1,000,000 rows (`All End Forces (2)` ...), split between members. The rows are written as they are produced and each sheet is streamed into the file, so a long table is not held in memory twice. The CSV in the package stays the full source.
> - **The envelopes** are over every case with printed forces. SRSS and ABS combinations are unsigned; keep them out of a load list that feeds an envelope where sign matters.
> - **Not there yet:** Reactions, Code Check and Displacements tabs. Their prints are named "Not read yet" on About until the probe's `.anl` gives their layouts (`scripts/lib/probes/`).

> [!info]- Verification (2026-11-24, Linux)
> - `tests/ModelData.Tests.ps1` (5):
>   - the workbook, its link formula, settings table and launcher text;
>   - every tab of Model_Data.xlsx;
>   - the envelope rows against a hand search of the package;
>   - the numbered tabs with a lowered limit;
>   - the `Anl file` setting;
>   - an `.anl` with errors writing nothing.
> - Both workbooks pass the Open XML SDK validator and open in LibreOffice.
> - The suite is 84 tests, 0 failed; the strict C# 5 compile is clean (CalcFlow alone; lib + arms + CalcFlow).
> - **Untested:** Excel following the link cell, the file dialog, the launcher under Windows PowerShell 5.1.
