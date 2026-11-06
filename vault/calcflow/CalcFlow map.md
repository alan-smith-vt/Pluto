---
title: CalcFlow map
status: current
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]]*

# CalcFlow map

Hub for `vault/calcflow/`: CalcFlow sets up and runs the standard steel connection calcs (beam to column, beam to beam, gusset, base plate, weld, bolt; locked Excel workbooks) from one STAAD analysis data set, instead of carrying inputs and results between files by hand. Code in `scripts/calcflow/` (and the frame reader in `scripts/lib/`).

- [[vault/calcflow/calcflow-overview|calcflow-overview]] — **start here**: the brainstorm's verdict (the calc library, the connection register, the analysis package), the ranked ideas, what was rejected, the roadmap, the open engineering questions, and what was taken from pipe-review.
- [[vault/calcflow/calcflow-staad-frame-reader|calcflow-staad-frame-reader]] — step 1, built: a STAAD `.anl` of a frame model → the analysis package (`Export-AnlFrame.ps1`): what is read and from where, STAAD's local axes, units, what is refused, the package's columns, the tests.
- [[vault/calcflow/calcflow-calc-library|calcflow-calc-library]] — step 2, built: the calc library (each locked template's cell map with label guards, units and bindings; the connection types, their steps, slots, sign maps and parameters), edited in Excel and run from its text export, and **harvest**, which learns a template's layout once and drafts its rows.
- [[vault/calcflow/calcflow-connection-register|calcflow-connection-register]] — step 2, built: the connection register (one row per member end) and the **plan** dry run that resolves every cell of every calc and names every gap; the planned DCR summary per member end with a joint rollup.
- [[vault/decisions/2026-11-06-calcflow-end-axes|2026-11-06-calcflow-end-axes]] — a connection's demand is taken in its member end's own axes (x away from the joint), so a frame holds whichever way a member was drawn; SRSS / ABS cases flagged.
- [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]] — the decision: three layers instead of one configuration workbook, the package as the only source of inputs, exact or flagged, and copying pipe-review's generic machinery instead of sharing it.
