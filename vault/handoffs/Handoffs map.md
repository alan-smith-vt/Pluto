---
title: Handoffs map
status: current
created: 2026-11-18
---

*↑ [[vault/Pluto Home|Home]]*

# Handoffs map

Hub for `vault/handoffs/`, and the handoff log: where Pluto stands, and what each session did. Read
[[vault/handoffs/handoff-current|handoff-current]] first.

**Each session:**
- writes its own dated record, `handoff-YYYY-MM-DD.md` (suffix `b`, `c`… for a second session the same day;
  updating today's is fine);
- adds a line for it here, newest first;
- refreshes `handoff-current`, a short current-state note with no history in it.

**No project names.** Like every file in this repo, a handoff names no project, and anything tied to a real job stays
in the Notes vault. The records before 2026-11-18 are there too (`Tools/Pluto Handoffs/`).

- [[vault/handoffs/handoff-current|handoff-current]] — the current state: what the branch holds, the check model's gaps, what waits on the production machine, what is next.
- [[vault/handoffs/handoff-2026-11-25|handoff-2026-11-25]] — branch `CalcFlow`: one full pass on a made-up frame (8 connection marks, 26 calcs through LibreOffice, DCRs checked against the templates' reference code); the run exposed harvest choosing several governing results and sign-led text read as formulas, both fixed; the circular weld tab needs Excel 365. Later the same day: `pipestress` merged in, and a template can take the support reaction instead of the member end forces (Templates tab, Forces); the first real `.anl` and workbook: a member list on `PRINT MEMBER FORCES` now reads, and table headings keep Excel's rules (Excel had repaired the tables; cause not confirmed, `excel-check` added to find it); then frozen key columns, a note beside every cell, and a tab per calc that holds the inputs of all its sheets, with a Template Sheets tab; last, a template's inputs read from the cells coloured as inputs, whatever its layout (first a preview, `map`, confirmed by the user on 133 template tabs; then the tabs built from it, with values typed per beam end and tabs only for the templates in use).
- [[vault/handoffs/handoff-2026-11-24b|handoff-2026-11-24b]] — a STAAD frame's `.anl` in the viewer (`StaadToPluto`, `scripts/staad/Run-StaadFrame.ps1`): members with STAAD's axes, sections from SAP's library or fitted to STAAD's member properties, supports; per load case end forces, displacements, reactions and code check ratios, a `CHECK CODE` under a one-case `LOAD LIST` going into that case. Checked on the probe's `.anl` through the viewer's reader (`readback_staad.js --probe`). Later: a model made in STAAD to the workbook and back to SAP or STAAD. Units converted to the first `UNIT`; the commands after the analysis (load lists, prints, design parameters) and STAAD's load numbers carried on two STAAD sheets; load envelopes as SAP Envelope combinations. Then `-Merge` (a SAP or STAAD file back into the master workbook, keeping what that program has no place for) and STAAD probe round 4 (units, envelopes, per-case checks, the whole triangle). Then round 4's results (units confirmed to the last printed digit; a load in two envelopes stays in both). Last: the STAAD design moved into the frame workbook itself, each member's parameters on its own Frames row; the separate `<model>.design.xlsx` of earlier that day is gone.
- [[vault/handoffs/handoff-2026-11-24|handoff-2026-11-24]] — branches `CalcFlow` and `pipestress`: CalcFlow merged with the latest pipestress and reviewed by three agents; the engineer's decisions (link cells, two files, marks with geometry on one schedule row); the frame `.anl` reader moved to pipestress with new gains and a probe model for the tables it does not read yet; CalcFlow's first front-door step (`CalcFlow.xlsx` Start tab, the post-process launcher, `Model_Data.xlsx`). Later that day: the load table (one calc per mark, only its beam ends), the code check, member tables and section displacements read and tabulated, Refresh config, a live unit check on Start, softer status colours.
- [[vault/handoffs/handoff-2026-11-23b|handoff-2026-11-23b]] — branch `CalcFlow`, step 3: `run` calculates the planned connection calcs with Excel or LibreOffice and keeps a record per check per revision, `summary` writes the DCRs per member end; harvest's layout split per template after Excel repaired the first real harvest; one colour scheme.
- [[vault/handoffs/handoff-2026-11-23|handoff-2026-11-23]] — a `.std` Pluto wrote reads back into a frame model (`Build-Frame -In x.std`, STAAD's edits too), through the `.anl` reader's own parsers extended in place (an `.anl` reads as before); the writer's comments carry SAP's names, and a writer bug (sections with modifiers of their own) is refused. On the production machine STAAD.Pro's own save (6 digits, its own layout) was refused at first; numbers are now checked to the file's digits and models compared object by object. Catalog sections: `-S2k` checks them against SAP's section library, and a `TABLE ST` shape picked in STAAD comes back from that library as SAP's catalog row.
- [[vault/handoffs/handoff-2026-11-21|handoff-2026-11-21]] — the code issues the vault audit found, fixed: the workbook overwrite question (both runners), a seed's additions keeping its SAP settings, the viewer's Auto range, Find on beams and labels, world coordinates with saved predicates kept; STAAD confirmed as an output only.
- [[vault/handoffs/handoff-2026-11-18|handoff-2026-11-18]] — the STAAD.Pro writer (three rounds) and the CAESAR → SAP check model, linear and nonlinear, verified against CAESAR 15.01 + SAP 24.2 over probe rounds 1–2d; OPE continues from SUS; what is left differs by friction's load path only. Then the vault audited against the code: a start-here note with diagrams, the hubs and the notes brought up to date, code issues listed in handoff-current.
