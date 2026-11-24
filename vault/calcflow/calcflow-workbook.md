---
title: CalcFlow workbook (Start, Model Config, Connection Lib, schedule tabs, Check, Run) and Model_Data.xlsx
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow workbook and model data

The front door of CalcFlow is a workbook. `CalcFlow.xlsx` has a **Start** tab whose link cells run the steps; the first step, **Post-process .ANL**, reads the STAAD analysis output and writes **`Model_Data.xlsx`** beside the workbook, with everything read from the model in tabs. Decided in [[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]].

The code is `scripts/calcflow/cs/Workbook.cs`, `ProjectBook.cs` and `ModelData.cs`, run through `CalcFlow.ps1 new` / `post-process` / `check` / `calcs` / `launchers`. Built 2026-11-24 and tested on the fixture. **Not yet run on the production machine:** the link cell, the file dialog and Windows PowerShell 5.1 are untested.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\calcflow\CalcFlow.ps1 new -Out "<project dir>" -Anl "<model>.anl" -Templates "<templates folder>"
# then open <project dir>\CalcFlow.xlsx and click "Post-process .ANL" on Start (or double-click "Post-process ANL.cmd")
```

> [!info]- The engineer's workbook and its launcher
> - **`new -Out <folder>`** writes `CalcFlow.xlsx` and `Post-process ANL.cmd`, once. An existing workbook is never overwritten: once saved it is the engineer's, and only Excel writes it.
> - **Start** lists the steps. Step 2 is a link cell, `=HYPERLINK("Post-process ANL.cmd", ...)`, relative to the workbook's folder. Excel asks before it opens a launcher; where links to files are blocked, the launcher is double-clicked.
> - **Settings** (`tblStart`): `Anl file`, the `.anl` to read. A name without a folder is looked for beside the workbook. Blank means a file dialog asks for it each time. A step reads the saved file, so save before running one.
> - **The launcher** runs `CalcFlow.ps1 post-process -Workbook "%~dp0CalcFlow.xlsx"` under Windows PowerShell and waits for a key. It finds the workbook beside itself, so the folder can move as a whole. Its path to `CalcFlow.ps1` is the one at write time: `CalcFlow.ps1 launchers -Workbook <CalcFlow.xlsx>` writes it again when the repo moved.
> - `Check.cmd` and `Run calcs.cmd` are written with it, and linked from Start the same way.

> [!info]- The engineer's tabs (Model Config, Connection Lib, the schedule tabs)
> `new -Out <folder> -Anl <file> -Templates <folder>` reads the model and learns the templates (harvest), then writes the workbook once:
> - **Model Config:** one row per beam of the model: Beam ID, Node I, Node J, Section, Shape, **Connection I, Connection J** (drop-downs of the marks), Frames into I / J (the member that end connects to, guessed first), Comment.
> - **Connection Lib:** one row per **mark**: Connection (a name of the engineer's own), Template, Description. A mark is one template plus one row of values; several marks may share a template.
> - **Templates:** one row per calc tab of a template workbook: File, Sheet, SHA-256, Loads (Envelope / All cases), Basis, Sign map. A sheet with fewer than five inputs gets no tab of its own.
> - **A schedule tab per template,** named like it. From the top:
>   1. **Source:** where each column's value comes from, a drop-down: Typed here; Template's own value; Fx to Mz (the beam end's force); Load case; Load case title; Beam number; Node; Beam section; Framed-into member; Framed-into section. A column whose label is one of the six force names starts as that force, unless its unit says otherwise (`Fy (ksi)` is a yield strength).
>   2. **Unit:** from the label where it has one; a force column must have one.
>   3. **Template value:** the template's own value, for reference.
>   4. the header: the template's own labels;
>   5. **one row per mark.** Every input is a column of that row. A blank cell keeps the template's value.
> - **Template Cells:** where each column goes in its template (reference; the tool reads it).
> - **Outputs:** harvest's results per template, with Use (Y on the ones read back; governing DCRs start as Y), Check (the summary column), Kind, Expect.
> - **Sign Maps** and **Load Cases** (Use, Basis).

> [!info]- Check and Run calcs
> - **`check -Workbook <CalcFlow.xlsx>`** (the Check link) reads the saved tabs and the current model, and lists what to fix by tab, in plain words: a mark with no row on its template's tab, a force column with no unit, a connection that is not on Connection Lib, a heading that was changed, a beam that is not in the model.
> - The tabs are expanded into the calc library's eight tables as text and a register, under `<workbook folder>\calcflow\generated\` (both generated; the planner, the run and the summary take them as before):
>   - a mark is a Type with one step on its template;
>   - a typed column is an optional `param:` input, and a typed cell a Params row of its mark;
>   - a sourced column is a `load:`, `case:` or `pkg:` input;
>   - every member end of the model is a register row, the ones with a connection carrying its mark.
> - Then the planner runs and writes `calcflow\plan`. Tabs are read by their cells, so a row typed below a table counts even where Excel did not grow the table over it. A template no mark uses is left alone.
> - **`calcs -Workbook`** (the Run calcs link) is check, then `run` and `summary` on that plan ([[vault/calcflow/calcflow-calc-stage|calcflow-calc-stage]]).
> - **Not yet:** a pasted block of load rows (beam, node, case, forces: many connections in one copy); small tables such as anchor coordinates as columns of the row (today each cell harvest found is its own column, and a grid of numbers is not found); section dimensions as a source; results written back into Model Config.

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
> - `tests/ProjectBook.Tests.ps1` (4): the tabs written from the fixture's `.anl` and the generic templates; a filled-in copy checked (two weld marks on one template with different leg sizes, a base plate mark); each thing to fix named; `calcs` through LibreOffice, each record holding its own mark's values.
> - The suite is 88 tests, 0 failed; the strict C# 5 compile is clean (CalcFlow alone; lib + arms + CalcFlow).
> - **Untested:** Excel following the link cell, the file dialog, the launcher under Windows PowerShell 5.1.
