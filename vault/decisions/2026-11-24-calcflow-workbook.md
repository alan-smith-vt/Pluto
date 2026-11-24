---
title: CalcFlow's front door is a workbook with link cells; model data is a generated second workbook; geometry sits on the schedule row
status: current
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

Decided by the engineer on 2026-11-24, after the review in [[vault/audits/calcflow-process-review|calcflow-process-review]]:

1. **The button is a link cell.** The Start tab of the engineer's workbook (`CalcFlow.xlsx`) holds `HYPERLINK` cells to `.cmd` launchers written beside the workbook. A launcher can always be double-clicked instead.
2. **Two files.** The engineer's workbook is written once and then saved only by Excel. Everything read from the `.anl` goes to a generated `Model_Data.xlsx` beside it, written again by every post-process run.
3. **Geometry inputs sit on the same row.** Each template gets a schedule tab with one row per connection mark. Every input the template needs is a column of that row, small tables included (anchor 1 x, anchor 1 y, anchor 2 x ...). A long row is fine. There are no child tabs per table.
4. **A connection name is a mark:** one template plus one row of values. Several marks may share a template, and Model Config picks a mark at each beam end.
5. **The `.anl` parser work is written to branch `pipestress`** (the lib), and `CalcFlow` merges it.

## Why

- **Link cells:** production machines block macros, and the tool cannot author a VBA project without Excel. Pluto's pipe and frame workbooks already work this way ([[vault/arms/workbook-kit|workbook-kit]]).
- **Two files:** Excel holds an open workbook, so no script can add tabs to it. A generated workbook can be rebuilt while the engineer's stays open.
- **One row:** the engineer reads a connection across one line, copies a row to make a variant, and filters or sorts 40 marks like any schedule. A child tab would split one connection over two places.
- **pipestress:** the readers belong to the lib that other arms use, so they should not fork on a feature branch.

## Consequences

- A small table of a template is written at its full capacity: the columns given on the row, then the empty-row value for the rest, which the library states and an engineer confirms. The number of anchors is how many of the pattern columns are filled.
- This partly supersedes "no single configuration workbook" in [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]]: the calc library and the register become tabs of the project's workbook. The eight tables stay as the exported, diffable text underneath.
- Tests of the frame reader stay on `CalcFlow` (`scripts/calcflow/tests/AnlFrame.Tests.ps1`), run against a build of the merged tree.

## Status

- **2026-11-24:** items 1, 2, 4 and 5 are built ([[vault/calcflow/calcflow-workbook|calcflow-workbook]]). Item 3 is built for single cells: every input harvest finds is a column of the mark's row. Small tables (anchor coordinates) as columns of the row, and the pasted load block, are next.
