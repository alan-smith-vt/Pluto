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
- [[vault/handoffs/handoff-2026-11-24b|handoff-2026-11-24b]] — a STAAD frame's `.anl` in the viewer (`StaadToPluto`, `scripts/staad/Run-StaadFrame.ps1`): members with STAAD's axes, sections from SAP's library or fitted to STAAD's member properties, supports; per load case end forces, displacements, reactions and code check ratios, a `CHECK CODE` under a one-case `LOAD LIST` going into that case. Checked on the probe's `.anl` through the viewer's reader (`readback_staad.js --probe`). Later: a model made in STAAD to the workbook and back to SAP or STAAD. Units converted to the first `UNIT`; the commands after the analysis (load lists, prints, design parameters) and STAAD's load numbers carried on two STAAD sheets; load envelopes as SAP Envelope combinations. Then `-Merge` (a SAP or STAAD file back into the master workbook, keeping what that program has no place for) and STAAD probe round 4 (units, envelopes, per-case checks, the whole triangle). Last: a STAAD design workbook (`<model>.design.xlsx`: parameters by group or member, checks and prints) beside the frame workbook, which keeps structure and loads.
- [[vault/handoffs/handoff-2026-11-24|handoff-2026-11-24]] — the frame `.anl` reader (`AnlFrameReader`, from branch `CalcFlow`) arrives in the lib with the indexer's command-end markers; it gains STAAD 2025's database section form, comment lines, `LOAD LIST` and a record of every output command, and checks that no force row goes missing; a probe model (`scripts/lib/probes/`) to run in STAAD for the tables not read yet. Later that day: the code check (three layouts), the member tables and the section displacements read too.
- [[vault/handoffs/handoff-2026-11-23|handoff-2026-11-23]] — a `.std` Pluto wrote reads back into a frame model (`Build-Frame -In x.std`, STAAD's edits too), through the `.anl` reader's own parsers extended in place (an `.anl` reads as before); the writer's comments carry SAP's names, and a writer bug (sections with modifiers of their own) is refused. On the production machine STAAD.Pro's own save (6 digits, its own layout) was refused at first; numbers are now checked to the file's digits and models compared object by object. Catalog sections: `-S2k` checks them against SAP's section library, and a `TABLE ST` shape picked in STAAD comes back from that library as SAP's catalog row. Then every steel family both ways in STAAD 2025's database form, single angles turned to their principal axes (confirmed end to end), STAAD's short PRIS forms, and frames SAP's auto mesh joins to joints split for STAAD.
- [[vault/handoffs/handoff-2026-11-21|handoff-2026-11-21]] — the code issues the vault audit found, fixed: the workbook overwrite question (both runners), a seed's additions keeping its SAP settings, the viewer's Auto range, Find on beams and labels, world coordinates with saved predicates kept; STAAD confirmed as an output only.
- [[vault/handoffs/handoff-2026-11-18|handoff-2026-11-18]] — the STAAD.Pro writer (three rounds) and the CAESAR → SAP check model, linear and nonlinear, verified against CAESAR 15.01 + SAP 24.2 over probe rounds 1–2d; OPE continues from SUS; what is left differs by friction's load path only. Then the vault audited against the code: a start-here note with diagrams, the hubs and the notes brought up to date, code issues listed in handoff-current.
