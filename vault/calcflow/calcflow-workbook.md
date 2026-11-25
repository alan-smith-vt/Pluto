---
title: CalcFlow workbook (Start, Model Config, Connection Lib, schedule tabs, Check, Run) and Model_Data.xlsx
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/calcflow/CalcFlow map|CalcFlow map]]*

# CalcFlow workbook and model data

The front door of CalcFlow is a workbook. `CalcFlow.xlsx` has a **Start** tab whose link cells run the steps; the first step, **Post-process .ANL**, reads the STAAD analysis output and writes **`Model_Data.xlsx`** beside the workbook, with everything read from the model in tabs. Decided in [[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]].

The code is `scripts/calcflow/cs/Workbook.cs`, `ProjectBook.cs` and `ModelData.cs`, run through `CalcFlow.ps1 new` / `post-process` / `check` / `calcs` / `refresh` / `launchers`. Built 2026-11-24 and tested on the fixture. **Not yet run on the production machine:** the link cell, the file dialog and Windows PowerShell 5.1 are untested.

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
> - **Force units** (Start, below the settings): one row per template tab, in formula cells, so it answers as the workbook is typed in and no step has to run. A template a connection uses says **OK** or **Fix** with the number of forces that have no unit (a column whose Source is Fx to Mz with a blank Unit; a load table with no Force unit or Moment unit). A template no connection uses says Not used. The tab's name is a link to it.
> - **Colours of a status or a check:** a soft fill with bold dark text of the same hue, as in the pipe-review workbooks (pale green for good, pale red for bad, pale yellow, orange, purple and grey for the states between). A DCR above the limit is bold dark red. The strong colours stay on the headers.

> [!info]- The engineer's tabs (Model Config, Connection Lib, the schedule tabs)
> `new -Out <folder> -Anl <file> -Templates <folder>` reads the model and learns the templates (harvest), then writes the workbook once:
> - **Model Config:** one row per beam of the model: Beam ID, Node I, Node J, Section, Shape, **Connection I, Connection J** (drop-downs of the marks), Frames into I / J (the member that end connects to, guessed first), Comment.
> - **Connection Lib:** one row per **mark**: Connection (a name of the engineer's own), Template, Description. A mark is one template plus one row of values; several marks may share a template.
> - **Templates:** one row per calc tab of a template workbook: File, Sheet, SHA-256, Loads (Envelope / All cases), Basis, Sign map, **Forces** (below); the load table of a template that has one (below); **Input cells** and **Result cells** (below). Which sheets become a tab: "Template sheets as calcs", below.
> - **Template Sheets:** every sheet of every template workbook, with the inputs and results found on it, the tab that holds its inputs, and what to do when it was not picked up. Written by the tool at every refresh; nothing on it is read back.
> - **A schedule tab per template,** named like it. From the top:
>   1. **Source:** where each column's value comes from, a drop-down: Typed here; Template's own value; Fx to Mz (the beam end's force); Load case; Load case title; Beam number; Node; Beam section; Framed-into member; Framed-into section. A column whose label is one of the six force names starts as that force, unless its unit says otherwise (`Fy (ksi)` is a yield strength).
>   2. **Unit:** from the label where it has one; a force column must have one.
>   3. **Template value:** the template's own value, for reference.
>   4. the header: the template's own labels;
>   5. **one row per mark.** Every input is a column of that row, however long the row gets. A blank cell keeps the template's value; `(blank)` empties the template's cell.
> - **Template Cells:** where each column goes in its template (reference; the tool reads it).
> - **Outputs:** harvest's results per template, with Use (Y on the ones read back), Check (the summary column), Kind, Expect. One result starts as Y only when exactly one reads as the governing DCR; otherwise the engineer chooses, and check asks for one.
> - **Cells kept as text:** the Fx to Mz cells of Sign Maps, Input cells and Result cells, and table columns are formatted as text, so `-Fz` or a table that starts with a minus sign is not read as a formula.
> - **Sign Maps** and **Load Cases** (Use, Basis).

> [!info]- Inputs from the cells coloured as inputs (2026-11-25)
> Harvest used to guess every template's inputs from its formulas and labels. That fits a form with a label beside each value. It does not fit a template laid out as a table under merged headings: each cell of the table becomes a column of its own, a neighbouring value is taken for a label, and every constant a formula reads becomes an input. The engineer confirmed on a library of 133 template tabs that reading the coloured cells matches what is filled in.
> - **The reading (`cs/TemplateMap.cs`):** an input is a cell with the input fill that holds no formula and can be seen (hidden rows, columns and sheets are left out). The input fill is the yellow family; a library with another colour names it once (`Input fill` on the Start tab, `-InputFill`). No layout is assumed. Cells that touch form a block: a cell, a row of cells, a list or a table. A merged cell counts once. A column is named by the heading above it, read across merged heading rows, and a lone cell by the label to its left, with the unit beside it.
> - **A workbook with such cells takes its inputs from them**, and nothing is guessed from its formulas; one with none is read the old way (said when the workbook is made).
>   - A cell, or each cell of a row of cells: a column of the template's tab, typed per mark. Where its heading names the node, the beam number, the load case or a force and its value agrees, the Source row starts as that.
>   - A list or table to type (anchor positions): one column whose cell holds the whole table.
>   - A table with a force, beam, node or load case heading: the template's load table, written by the tool a row at a time (Templates tab: its first cell, its columns, its rows). The formulas along its rows are its results, one a row; the ratios start with Use = Y.
> - **The table's columns are on the template's tab** (teal headings, after the columns typed per mark): each with its heading in the template, its first value, its Unit, and in the Source row what goes in it (Beam number, Node, Load case, Fx to Mz, Typed per beam end, Typed here, or the template's own value for a column left alone). The tab is where that is changed; `Load table columns` and the two units on Templates are written again from it by Refresh config. Nothing is typed under such a column, unless its Source is Typed here: then the mark's row holds the value, and it goes on every row of the mark's table (a value of the configuration, the same at each beam end; `{Grade}` in `Load table columns`). A template whose only coloured cells are its table used to get a tab with the one column Connection, which read as nothing found.
> - **A tab says first what it is:** "This template is a table of load rows ..." or, where neither a cell nor a table was found on the sheet, "NO INPUT WAS FOUND ..." with what to do (Input fill, Input cells); Refresh config says the second too.
> - **Typed per beam end:** a column of such a table that the model cannot fill has that Source (in `Load table columns` it is written in square brackets, `[Stiffened?]`). The template then has a tab of beam ends (`<template> ends`): a row per beam end that calls one of its marks, listed by Refresh config from Model Config, what was typed kept by beam and end. `check` asks for a refresh when a beam end has no row.
> - **Loads** has a third choice for a table of one governing row a beam end: `Largest of each force` (not at one time).
> - **A library of more than 20 calc sheets** gets no tab per template when the workbook is made: a template's tab is made by Refresh config once a mark on Connection Lib uses it. Templates and Template Sheets list them all.
> - **`CalcFlow.ps1 map -Templates <folder> -Out <folder> [-InputFill <colour>]`** writes `Template_Map.xlsx` to look the reading over: a count per template sheet, every input found with the few to look at marked, and every fill colour found.
> - **Checked on:** made-up templates of three layouts taken from `new` to the DCRs through LibreOffice (the tests), and the generic templates with their own input colour named. Not seen in Excel, and not on the engineer's templates beyond the `map` look.
> - **Still by hand:** an input that is not coloured (Input cells on the Templates tab); what one row of a table means when the headings do not say.

> [!info]- Template sheets as calcs (which sheets get a tab)
> A mark is one tab and one row, so a tab must reach every input of its calc, on whichever sheet of the workbook it sits. Harvest records which sheet's formulas read which, however far (through names and tables too).
> - **One calc:** sheets that read one another (a base plate's main sheet, its anchorage sheet and its helper sheets). One tab, named for the sheet with the most inputs. That sheet's inputs come first under their own labels; the other sheets' follow as `Sheet: label`.
> - **A part of several calcs:** a sheet that is only read (shared values under six weld shapes). Its inputs are columns of every tab that reads it, and it gets no tab itself.
> - **Calcs of their own:** sheets that read nothing of one another (one weld shape a sheet). A tab each.
> - **A workbook with a load table** is one calc per table, with the inputs of every sheet (as before).
> - **Too small to be a calc:** a set of sheets with fewer than five inputs and no result found gets no tab, unless it is all the workbook has. The Template Sheets tab says so on its row, and how to give it one (a row on Templates with its File and Sheet).
> - **Until 2026-11-25** each sheet with five inputs or more was a tab of its own and the inputs of smaller sheets were on no tab: a base plate mark could not reach its anchorage sheet's inputs. A workbook made then keeps its tabs at a refresh, and its calc tabs gain the missing columns.
> - **Results:** a tab lists the results of its calc's sheets, and of the workbook's sheets that take no input. When several read as the governing DCR, the one on the tab's own sheet is chosen.

> [!info]- Notes beside the cells, and what stays in view
> - **Notes (2026-11-25):** selecting a cell shows a note beside it: what the cell is and what is expected in it. Every column of every tab has one, on its heading too, and so have the Start tab's settings and the Source and Unit rows of a schedule tab. A schedule column's note names its template cell and the template's own value. They are the input messages of data validations, as in the pipe-review workbooks: a column with a drop-down carries its note on the drop-down (`cs/Hints.cs`).
> - **Frozen (2026-11-25):** every tab keeps its heading rows in view, and the leading columns that say what the row is: the template on Templates, the mark on a schedule tab, the beam on Model Config, the case and its title on Load Cases, the member and its end in Model_Data.xlsx. At most four columns, and no wider together than leaves room to scroll.
> - **Excel's limits** for a note (a title of 32 characters, a text of 255, no cell under two validations) are kept by the writer and checked over every workbook the tests write (`XlsxCheck.Validations`).

> [!info]- Colours that follow the choices (2026-11-25)
> A cell changes colour as things are chosen on the tabs (conditional formats, `cs/Needs.cs`): what `check` says in words, said while the tab is filled in. The Start tab has the legend.
> - **Still needed** (a stronger yellow than an input cell's own):
>   - Connection Lib: a mark with no template, a template with no mark, a mark with no row yet on its template's tab;
>   - a template's tab: the Unit of a column whose Source is a force or a moment; a value under a table column whose Source is Typed here, in a mark's row; the Connection cell of a row that has values and no mark;
>   - a tab of beam ends: a blank value in a beam end's row;
>   - Templates: Max rows of a load table; for a table said only on this tab, its columns and the units of the forces it takes;
>   - Outputs: the Use cells of a template a mark uses when none of its results has Use = Y; Expect of a health result in use;
>   - Load Cases: the Use column while no case is in use; Start: the templates folder.
> - **Not read** (grey): in a mark's row, a column whose Source is the model's or the template's own value; on Templates, the load table cells the tool writes where the template's tab shows the table.
> - **Refresh config** (orange): a mark whose template has no tab yet; a table column turned to Typed per beam end that the tab of beam ends does not have yet.
> - **Does not fit** (red): the same mark twice; a mark at a beam end that is not on Connection Lib; a mark on a template's tab that is not one of that template's; a template that is not on the Templates tab.
> - **Not coloured:** a cell typed for a mark and left blank (blank keeps the template's own value, so nothing is missing); Frames into (whether it is needed depends on the template's columns, which a formula cannot follow); a mark used at no beam end.
> - **How they are written:** each rule is a formula for the first cell of its range. Another tab is reached through `INDIRECT` (a sheet named outright in such a formula is refused by Excel 2007 and kept elsewhere in the file by later versions), no array constant, 255 characters at most; the writer refuses a formula that breaks these, and `XlsxCheck.FormatRules` (in `excel-check` and over every workbook the tests write) reads them back.
> - **Checked:** LibreOffice calculates every rule's formula, in a finished project (none asks for anything) and in copies with things missing (each fires for the choice it watches), and shows the colours. Not seen in Excel.

> [!info]- Headings, and Excel's rules for a table
> Every tab's rows are an Excel table, and a schedule tab's headings are the template's own labels. Excel has rules for a table's headings that LibreOffice and the Open XML schema check do not apply. A workbook that breaks one opens in Excel with "Repaired Records: Table from /xl/tables/tableN.xml part (Table)".
> - **The rules:** a heading is text, on one line, of 255 characters at most, the same in its cell and in the table part, and no two headings of a table are alike.
> - **What the tool does (2026-11-25):** a label is put on one line with single spaces; one over 255 characters (a note beside an input) is cut and ends in ` ...`; two that are alike get the cell in brackets on the second (`D (in) [B15]`). Alike is taken widely, since Excel's comparison is not written down: letter case, spacing, and the forms of a character that look the same (the ohm sign and omega, a superscript 2 and 2). The writer puts every table's heading cells right and refuses two alike (`Xlsx.TableHeading`, `Xlsx.Headings`).
> - **The check:** `cs/XlsxCheck.cs` reads a written workbook's tables against these rules. The tests run it over every workbook they write, beside the schema validator and the LibreOffice open check.
> - **For a workbook Excel repaired:** `CalcFlow.ps1 excel-check -Workbook <it, as written>` lists what in its tables breaks a rule. With `-Repaired <the copy saved from Excel after the repair>` it also lists what Excel changed, heading by heading, which names the rule when the check itself finds nothing.
> - **Not seen in Excel here:** the first report came from the production machine, with templates this repo does not hold. A heading over 255 characters is the one way found to make the tool write such a table.

> [!info]- Forces: member end forces, or the support reaction
> The Templates tab's **Forces** column says what a template is given, per template, from a list of two.
> - **Member end forces** (the start value): the printed end forces of the beam end the mark sits at.
> - **Support reaction:** the printed reaction at that beam end's joint. It is for an anchorage where more than the column lands on the base (a brace), since the column's own end forces leave the other members out.
> - **Same axes, same sign map.** The reaction is given in the axes of the beam end the mark sits at (x along the member away from the joint, y the member's local y, z = x cross y) and taken as a force on the member, like an end force. Where a column stands alone on its support the two are the same numbers. On the probe's real `.anl` they agree to the printed digit; at its braced bases the moments agree and the forces differ by the brace.
> - **Units:** reactions printed in other units than the member forces are converted.
> - **Refused, with the reason:** the joint is not a support; a load case in use has no printed reaction (print the reactions under the same `LOAD LIST` as the member forces).
> - **In the files:** the step's Slot is `reaction` in the generated library text, and each row of `plan\demands.csv` says `support reaction at joint N`.
> - A blank cell, or a workbook made before the column existed, means Member end forces. `refresh` adds the column.

> [!info]- A template that takes a table of load rows
> Some templates take a table: one row per beam end and case, with the beam, the node and the case beside the forces. Such a template is **one calc per mark**: the mark's copy lists every beam end that calls the mark on Model Config, and no other.
> - **Found by `new` / `refresh`:** a header row with three or more force names (Fx to Mz, with or without a unit) in adjacent columns, over cells that are typed. Forces a template calculates for itself (per bolt, per weld segment) and single labelled inputs (`Fx | value | Fy | value`) are not a load table.
> - **On the Templates tab:** `Load table at` (its first cell), `Load table columns` in order (Beam, Node, LC, LC title, Fx to Mz, Section, Connection, Mark, or `-` for a column to leave alone), `Max rows`, `Force unit`, `Moment unit`. All of it can be typed or corrected by hand.
> - **On the template's own tab (2026-11-25):** where the table was found in the template, its columns are shown there and the tab's Source and Unit rows are what `check` reads; the columns and units on Templates repeat them. A table given by hand on Templates (one the tool did not find) is read from Templates, as before. A workbook saved before the tab showed the table gets the columns at its next Refresh config, as its Templates row had them.
> - **Rows:** each beam end gives its own rows: every case in use (Loads = All cases), or its own envelope (Loads = Envelope). Rows the table does not need are emptied, so a template's sample rows never stay in a record. More rows than `Max rows` are calculated in several fills of the template.
> - **Results beside the table:** on Outputs, `Per row` = Y and the first cell of the result's column. Each beam end is reported with the largest of its own rows.
> - **The schedule tab** of such a template carries the inputs of all its sheets (the table's sheet first, the others as `Sheet: label`).
> - **An Excel data table** (What-If) is not calculated row by row by LibreOffice: every row gets the same value. A template built on one is refused under the LibreOffice engine and needs Excel.

> [!info]- Input cells, Result cells, and a block typed as one table
> Harvest guesses a template's inputs: unlocked or validated cells and constants a formula reads. It leaves out a grid of numbers (anchor coordinates, a bolt pattern). Where the guess is not what is wanted, the cells are given by hand on the Templates tab, and `refresh` rebuilds the tab from them:
> - **Input cells:** `B4, B6:B9, Sheet2!C3`. A range is its cells, each a column. A name before `=` names the column (`Edge = B7`); otherwise the label left of the cell, or above it, does. The list replaces what harvest found; start it with the word `also` to add to it instead (`also Anchors = table B37:D44`).
> - **A block as one table:** `Anchors = table A5:B10`. The block is one column, and a mark's cell holds the whole table: rows separated by `;` (or a line break in the cell), values by `,`, for example `2, 2; 2, 10; 10, 2; 10, 10`. The rows the mark does not give are emptied, so a mark with four anchors in a block of twelve leaves eight empty rows. A table too long or of another width is said by check.
> - **Result cells:** `E1, E3` or `Uplift = E7`. Each becomes a row of Outputs with Use = Y. A cell harvest reads as a ratio is a dcr; another is a `value` (reported as it is): set its Kind on Outputs where it is a ratio.
> - A cell that holds a formula is refused as an input. A template sheet harvest gave no tab can be added as a Templates row by hand (Template, File, Sheet, Input cells).

> [!info]- Refresh config, and what changed
> A saved workbook is the engineer's and only Excel writes it, with one exception that is asked for: **Refresh config** (Start, "When needed"; `Refresh config.cmd`; `CalcFlow.ps1 refresh -Workbook`).
> - **When:** a template was added to the folder or changed; the model has new beams or load cases; Input cells or Result cells were given. `post-process` and `check` say so, as notes, when it is due: a template file the Templates tab does not list, one whose content changed (by its SHA-256), one that is gone; beams and load cases the tabs lack; given cells that are not columns yet.
> - **What it does:** the workbook is written again around the model (the last post-process) and the templates folder as they are now, and everything filled in is carried over:
>   - a template is known again by its file and sheet, and keeps its name, its Templates settings and its schedule tab;
>   - a schedule column is known by its heading: its Source, its Unit and every mark's value follow it, also when the template's cell moved;
>   - a beam by its number (its connections, Frames into, comment), a load case by its number (Use, Basis), a result by its template, sheet and cell;
>   - Connection Lib, Sign Maps and the settings as they are; a column of the engineer's own on a tab stays.
> - **What it says:** each new template, beam and load case; a beam that is no longer in the model (with the connections it had); a schedule column the template no longer has (kept at the end of the tab with its values, to be moved by hand); a template whose file is gone (its tab is kept as it was); a tab that is not one of CalcFlow's (not carried over).
> - **Nothing is lost:** the workbook as it was is kept beside it, `CalcFlow (before refresh <date>).xlsx`.
> - **Excel must have closed the workbook.** The step is started from inside it, so it waits: "save it and close it now", then it refreshes and opens the workbook again. `-NoWait` refuses instead.

> [!info]- Check and Run calcs
> - **`check -Workbook <CalcFlow.xlsx>`** (the Check link) reads the saved tabs and the current model, and lists what to fix by tab, in plain words: a mark with no row on its template's tab, a force column with no unit, a connection that is not on Connection Lib, a heading that was changed, a beam that is not in the model.
> - The tabs are expanded into the calc library's eight tables as text and a register, under `<workbook folder>\calcflow\generated\` (both generated; the planner, the run and the summary take them as before):
>   - a mark is a Type with one step on its template;
>   - a typed column is an optional `param:` input, and a typed cell a Params row of its mark;
>   - a sourced column is a `load:`, `case:` or `pkg:` input;
>   - every member end of the model is a register row, the ones with a connection carrying its mark.
> - Then the planner runs and writes `calcflow\plan`. Tabs are read by their cells, so a row typed below a table counts even where Excel did not grow the table over it. A template no mark uses is left alone.
> - **`calcs -Workbook`** (the Run calcs link) is check, then `run` and `summary` on that plan ([[vault/calcflow/calcflow-calc-stage|calcflow-calc-stage]]).
> - **Not yet:** section dimensions looked up from the shape (typed for now); a check that the model's member forces were printed for every case in use.

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
> | Reactions | Node, LC, Fx to Mz: the support reactions as STAAD printed them, in the global axes (when the file prints them) |
> | Reaction Extremes | per support: the largest and smallest of each reaction component. These do not act together |
> | Member Ends | one row per member end at a joint: Role, Section, the member it runs through |
> | Load Cases | LC, Kind, Method, Load Type, Title, Factors, Forces printed |
> | All End Forces | Member, End (I / J), Node, LC, Fx to Mz as STAAD printed them, in the member's local axes |
> | Enveloped End Forces | per member end and component: the case of the largest and of the smallest value, with the forces that act with it (12 rows an end) |
> | End Force Extremes | per member end: the largest and smallest of each component on one row. These do not act together |
> | Joint Displacements | Node, LC, X Y Z and the rotations (rad), as printed (when the file prints them) |
> | Code Check | one row per member per `CHECK CODE` command: the code, PASS / FAIL / NOT DESIGNED, ratio, governing clause, case and location, the forces of that case; **Governs = Y** on a member's largest ratio over the checks of the file |
> | Code Check Details | the checks behind a result (demand, capacity, ratio, clause, case), where TRACK 1 or 2 printed them |
> | Member Information | joints, length (offsets taken off), beta and releases as STAAD printed them |
> | Member Properties | AX IZ IY IX AY AZ SZ SY as STAAD printed them |
> | Section Displ. Maxima | per member: STAAD's largest local displacement, where, in which case, and the span over it |
> | Section Displacements | global X Y Z at each of the 13 stations along a member, per case (split like All End Forces) |
>
> - **Ends** A / B of the package are shown as I / J.
> - **Size:** a long table continues on numbered tabs past 1,000,000 rows (`All End Forces (2)` ...), split between members. The rows are written as they are produced and each sheet is streamed into the file, so a long table is not held in memory twice. The CSV in the package stays the full source.
> - **The envelopes** are over every case with printed forces. SRSS and ABS combinations are unsigned; keep them out of a load list that feeds an envelope where sign matters.
> - **Every usual output command is read** (the probe's real `.anl`, `scripts/lib/probes/`). One the reader does not know is named "Not read yet" on About.

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
> - On the probe's real `.anl` (STAAD.Pro 2025): 36 reaction rows and 90 displacement rows, supports x cases and joints x cases of their load lists.
> - `tests/Refresh.Tests.ps1` (9): the unit check as written and as LibreOffice calculates it; the notes of post-process and check; a refresh that adds a template and a beam and carries everything over, after which check finds the same calcs ready; an orphaned column; a workbook Excel has open refused; given Input cells and Result cells; a table typed in one cell run through LibreOffice for two marks.
> - `tests/ProjectBook.Tests.ps1` also covers the load table: found, one calc per mark with only its beam ends, each row's DCR, the unused rows emptied.
> - On the probe's real `.anl`, Model_Data.xlsx holds 112 code check rows, 654 detail rows, 32 members of information and properties and 2,080 section displacement rows.
> - The suite is 108 tests, 0 failed; the strict C# 5 compile is clean (CalcFlow alone; lib + arms + CalcFlow).
> - **Untested:** Excel following the link cells, the file dialog, the launchers under Windows PowerShell 5.1, the refresh waiting for Excel to close the workbook, and the Force units formulas in Excel itself (LibreOffice calculates them).
