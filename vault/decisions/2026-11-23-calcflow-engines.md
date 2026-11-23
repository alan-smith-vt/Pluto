---
title: CalcFlow calculates with Excel where it is installed, else LibreOffice; each record names its engine
status: draft
created: 2026-11-23
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

**CalcFlow's calc stage has two engines behind one interface**, a batch of copies in and the same copies recalculated out:
- **Excel** through COM, in a Windows PowerShell 5.1 child stage;
- **LibreOffice** headless (open source).

`-Engine auto` (the default) takes Excel where its COM class is registered, else LibreOffice, else it refuses with both named. **Every record and every state row names the engine and its version.**

**The inputs are written at the XML level, for both engines.** A copy of the template gets the planned cells (`XlsxPatch`), its calc chain dropped and full calculation on load set. Neither engine writes a cell through its own API.

**The record is the calculated copy the governing DCR was read from**, not a second save of the case. Checks with the same governing case share one record.

**Where another tool was needed, an open-source one was used**, on the development side only:
- the Open XML SDK validator for the schema;
- LibreOffice for the open check and the PDF;
- poppler's `pdftoppm` for previews.

None of them is needed on the production machine.

## Why

- **The engineer asked** for LibreOffice where Excel is needed, and open-source tools elsewhere. Auto keeps Excel, the engine the templates were built and checked in, wherever it exists.
- **One input path for both engines.**
  - Writing cells at the XML level is the same code for both, needs neither engine's API, and cannot be stopped by sheet protection (a locked template's input may sit on a protected sheet).
  - Excel then only opens, calculates and saves: the smallest COM surface, and it is read-only, so a write-reservation password never prompts.
- **The record is what was read.** Keeping the very copy the DCR came from leaves nothing to reproduce. The earlier plan was to write the governing case again and compare; that is not needed when the copy is kept.
- **Naming the engine** keeps a LibreOffice result from passing for an Excel one in a review, and tells a later reader which to rerun with.
- **The numbers agree.** On the generic templates, LibreOffice's records match EngCalc-Templates' own Python references to about 1e-15 (the weld group's rectangular tab, and the base plate).

## Consequences

- An Excel run opens one copy per case. That is fine for envelopes, but slow for a long `per_combo` step. Writing many cases into one open workbook would be faster, but would need COM writes into protected sheets.
- A real template may use something LibreOffice evaluates differently (iterative calculation, some array functions, macros). Each real template should be run once with both engines and compared before LibreOffice results are relied on.

## Status

- **2026-11-23:** built.
  - LibreOffice is tested end to end on the fixture project.
  - The Excel stage is written and parse-checked but has not run: it needs `Excel.Tests.ps1` on the production machine.
  - See [[vault/calcflow/calcflow-calc-stage|calcflow-calc-stage]].
