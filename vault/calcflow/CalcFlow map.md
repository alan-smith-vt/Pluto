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
- [[vault/decisions/2026-11-06-calcflow|2026-11-06-calcflow]] — the decision: three layers instead of one configuration workbook, the package as the only source of inputs, exact or flagged, and copying pipe-review's generic machinery instead of sharing it.
