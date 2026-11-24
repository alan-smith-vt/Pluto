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
- [[vault/calcflow/calcflow-connection-register|calcflow-connection-register]] — step 2, built: the connection register (one row per member end) and the **plan** dry run that resolves every cell of every calc and names every gap (and stamps the sources it was made from).
- [[vault/calcflow/calcflow-calc-stage|calcflow-calc-stage]] — step 3, built: `run` calculates the due calcs with Excel or LibreOffice (cells written into copies, a record per check per revision, results and state, carry-forward of unchanged calcs) and `summary` writes the DCRs per member end with a joint rollup; the open-source checks; the cross-check against EngCalc's Python references.
- [[vault/calcflow/calcflow-workbook|calcflow-workbook]] — **the front door**, built 2026-11-24: `CalcFlow.xlsx` (Start with link cells to launchers; Model Config, one row per beam with a connection mark at each end; Connection Lib; a schedule tab per template with one row per mark; Check and Run calcs), and the generated `Model_Data.xlsx` (members, joints, supports, load cases, all end forces, enveloped end forces; what is not read yet named on About).
- [[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]] — the engineer's decisions after the review: link cells, two files, geometry on the schedule row, marks, parser work on `pipestress`.
- [[vault/audits/calcflow-process-review|calcflow-process-review]] — the 2026-11-24 review of all of the above against the engineer's sketch of a GUI-like workbook: what is too heavy, what the `.anl` reader does not read yet, and the proposed revamp (not decided).
- [[vault/decisions/2026-11-23-calcflow-engines|2026-11-23-calcflow-engines]] — Excel where it is installed, else LibreOffice, behind one interface; inputs written at the XML level for both; the record is the copy the DCR was read from; each record names its engine.
- [[vault/decisions/2026-11-06-calcflow-end-axes|2026-11-06-calcflow-end-axes]] — a connection's demand is taken in its member end's own axes (x away from the joint), so a frame holds whichever way a member was drawn; SRSS / ABS cases flagged.
- [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]] — the decision: three layers instead of one configuration workbook, the package as the only source of inputs, exact or flagged, and copying pipe-review's generic machinery instead of sharing it.
