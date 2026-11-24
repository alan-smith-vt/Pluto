---
title: Audits map
status: current
created: 2026-09-03
---

*↑ [[vault/Pluto Home|Home]]*

# Audits map

Hub for `vault/audits/` — read-only assessments made before a merge or port. The sanitized-lib
audit and its raw agent output moved to the Notes vault (`Tools/Pluto Sanitized Audit`).

- [[vault/audits/ring-wall-gap-audit|ring-wall-gap-audit]] — the ring wall gap links (`GAP_CONTACT`, `GAP_SOIL`): ids per spoke, property facts, and nine SAP2000 GUI checks in order with the answer each should give; desk findings, including that the wall frames were stiffness-transformed all along (`Transform` column).
- sanitized-lib audit (Notes vault, `Tools/Pluto Sanitized Audit`) — the transcribed STAAD lib: per-file damage table, the **re-hydration checklist** to carry into the production environment, compile status (compiles since 2026-09-03 with three throwing stubs), C# 5 compliance.
- [[vault/audits/calcflow-process-review|calcflow-process-review]] — CalcFlow as built through step 3, reviewed by three agents against the engineer's sketch of a GUI-like workbook (2026-11-24): usability findings and what each concept should become, the `.anl` reader's coverage of the usual print commands with its gaps and hazards, scale, and a proposed setup (Start links, Model Config per beam, connection marks with schedule tabs, a generated model-data workbook) with its build order. Proposed, not decided.
- [[vault/audits/sapviewer-audit|sapviewer-audit]] — the SapViewer repo before fold-in: what was new (the SAP arm), what was a fork of the old viewer (dropped), what was superseded. Status note at the top records the outcome.
