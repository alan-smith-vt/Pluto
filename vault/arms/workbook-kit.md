---
title: Workbook kit (the machinery the CAESAR and SAP frame workbooks share)
status: current
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Workbook kit

`scripts/arms/workbook/Wb*.cs` is the machinery shared by:
- the CAESAR pipe workbook ([[vault/arms/pipe-workbook|pipe-workbook]]);
- the SAP frame workbook ([[vault/arms/frame-workbook|frame-workbook]]).

It was extracted on 2026-11-05 rather than copied, so a fix lands in both
([[vault/decisions/2026-11-05-frame-model|2026-11-05-frame-model]]). Each workbook keeps its own sheets, columns, kinds
and texts, and hands them to the kit.

> [!info]- The pieces
> | Type | File | What it does |
> |---|---|---|
> | `WbTheme` | `WbForm.cs` | Cell styles and conditional formats, created in a fixed order. Header colour groups are given by the workbook. |
> | `WbCol`, `WbForm` | `WbForm.cs` | A column (name, group, kind, list, hint, note). `Table` puts a column table on a sheet. Built-in kinds: `id text num pos nonneg int0 cos yesno list! list? ref`. A workbook adds its own kinds through `KindRule` / `KindValidation` (CAESAR: `node catalog vector radius elemref`). `RefRows` is per book (it was a static). |
> | `WbStart` | `WbStart.cs` | The Start sheet (`Texts` holds the workbook's wording) and its run block (`WbLink`, `WbCommand`); `WbSheetInfo`, `WbCheck`. |
> | `WbCalc` | `WbStart.cs` | A hidden helper sheet with one row per input row: `At`, `Range`, `Count`, `Last`, and row templates (`Expand`: `[k] [k-] [k+] [k:] [k^] [k*]`). |
> | `WbRun` | `WbRun.cs` | Launchers beside the workbook and the Start sheet's command formulas. A workbook's subclass names its runner and launchers (`PipeWorkbookRun`). |
> | `WbBase` | `WbData.cs` | The hidden base model (`_pluto`), fingerprints, and `Same`. |
> | `WbRead`, `WbCols`, `WbReadCtx` | `WbData.cs` | Columns by header (the unit in brackets apart), cell text with its problem reported, `Raw` numbers and the header unit, list entries, Ids. |
> | `WbRefresh` | `WbData.cs` | The layout rebuilt with typed cells kept (formulas re-pointed, user columns and sheets kept, settings by name). |
> | `WbFormula` | `WbFormula.cs` | Values for formulas a script library saved without them. The workbook gives its input sheets. |
>
> Reused as they are:
> - `XlsxWriter.cs` (`XlsxBook`, `XlsxBookSheet`, styles, validations, charts);
> - the xlsx reader in `CaesarResults.cs` (`CaesarWorkbook`, `CaesarSheet`, `XlsxFormulaRefs`), generic despite its name.
>
> Still in the CAESAR workbook:
> - its fixed Lists layout (the `L*` constants);
> - the 3D View sheets. Their generic parts (`WbPlot`) move out when the frame workbook draws its own.

> [!info]- The gate
> The extraction had to leave every CAESAR workbook byte-identical. A mono harness writes 38 workbooks and records the
> model each one reads back as:
> - 13 test models, 2 examples, the sample `.cii` and the template;
> - each of those with and without the run block;
> - four refreshes: the sample and the template as written, an `e1` edited and saved by openpyxl (formulas with no
>   cached value), and an `e5` recalculated by LibreOffice.
>
> All four steps reproduced the baseline exactly, checked 2026-11-05:
> 1. form;
> 2. Start, run and calc;
> 3. base, read and refresh;
> 4. the formula evaluator.
>
> Launcher text and the Start formulas were compared separately: also identical. `Build-Pipe.ps1` `-Xlsx`, `-Refresh`
> and `-Cii` run under pwsh 7 through the `PLUTO_DLL` hook, now in `Build-Pipe.ps1` too. The PS 5.1 compile on the
> production machine is still to do.
>
> Byte identity depends on call order: style creation, then the conditional / validation / note / link order per sheet,
> then the sheet order. An adapter calls the kit in the old order.
