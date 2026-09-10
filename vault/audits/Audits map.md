---
title: Audits map
status: current
created: 2026-09-03
---

# Audits map

*↑ [[vault/Pluto|Home]]*

Hub for `vault/audits/` — read-only assessments made before a merge or port. Raw agent
output (per-file JSON, compiler records, journals) sits in `audits/raw/` and is not linked.

- [[vault/audits/ring-wall-gap-audit|ring-wall-gap-audit]] — the ring wall gap links (`GAP_CONTACT`, `GAP_SOIL`): ids per spoke, property facts, and nine SAP2000 GUI checks in order with the answer each should give; desk findings, including that the wall frames were stiffness-transformed all along (`Transform` column).
- [[vault/audits/scripts-sanitized-audit|scripts-sanitized-audit]] — the transcribed STAAD lib: per-file damage table, the **re-hydration checklist** to carry into the production environment, compile status (compiles since 2026-09-03 with three throwing stubs), C# 5 compliance.
- [[vault/audits/sapviewer-audit|sapviewer-audit]] — the SapViewer repo before fold-in: what was new (the SAP arm), what was a fork of the old viewer (dropped), what was superseded. Status note at the top records the outcome.
