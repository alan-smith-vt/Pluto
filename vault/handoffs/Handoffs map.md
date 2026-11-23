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
- [[vault/handoffs/handoff-2026-11-23|handoff-2026-11-23]] — a `.std` Pluto wrote reads back into a frame model (`Build-Frame -In x.std`, STAAD's edits too), through the `.anl` reader's own parsers extended in place (an `.anl` reads as before); the writer's comments carry SAP's names, and a writer bug (sections with modifiers of their own) is refused.
- [[vault/handoffs/handoff-2026-11-21|handoff-2026-11-21]] — the code issues the vault audit found, fixed: the workbook overwrite question (both runners), a seed's additions keeping its SAP settings, the viewer's Auto range, Find on beams and labels, world coordinates with saved predicates kept; STAAD confirmed as an output only.
- [[vault/handoffs/handoff-2026-11-18|handoff-2026-11-18]] — the STAAD.Pro writer (three rounds) and the CAESAR → SAP check model, linear and nonlinear, verified against CAESAR 15.01 + SAP 24.2 over probe rounds 1–2d; OPE continues from SUS; what is left differs by friction's load path only. Then the vault audited against the code: a start-here note with diagrams, the hubs and the notes brought up to date, code issues listed in handoff-current.
