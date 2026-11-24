---
title: Pluto Home
status: current
created: 2026-09-10
---

*↑ [Pluto — tool note in the Notes vault](obsidian://open?vault=Notes&file=Tools%2FPluto)*

# Pluto Home

This stub exists so the repo works as a standalone vault. The full description of Pluto (what it solves, how to run it, limits, projects, code-folder descriptions, conventions) lives in the Notes vault's tool note; the link above opens it in that vault (it also works when this repo sits inside Notes, where the same note is `Tools/Pluto.md`). Read the current handoff first for the current state: [[vault/handoffs/handoff-current|handoff-current]].

## Maps

**Start here for pipe stress and frame models** (CAESAR II, SAP2000, STAAD.Pro):
[[vault/arms/pipe-and-frame-workflow|pipe-and-frame-workflow]], the pipeline in diagrams and the commands for each job.

- **[[vault/arms/Arms map|Arms map]]** — one usage note per arm: the plant exports (pipe CSV, steel CSV, combined), pipe stress (the CAESAR workbook and TOML builder, CAESAR's checks, CAESAR results), frame models (the frame workbook), and SAP2000 (the controller, exports, the tank).
- **[[vault/format/Format map|Format map]]** — the common model (v4 binary schema, the frozen v3 schema, the features sidecar, the C# writer), and the engineering models and solver files: the pipe model, the frame model, CAESAR's neutral file, SAP's `.s2k` tables, STAAD's `.std`, the CAESAR → SAP check model.
- **[[vault/viewer/Viewer map|Viewer map]]** — the viewer's features and tests, several models in one scene (the model rail), and the old-viewer merge plan.
- **[[vault/audits/Audits map|Audits map]]** — read-only audits.
- **[[vault/decisions/Decisions map|Decisions map]]** — design decisions, one dated note each.
- **[[vault/calcflow/CalcFlow map|CalcFlow map]]** — steel connection calcs set up and run from one STAAD analysis data set: the design and roadmap, the STAAD `.anl` frame reader and analysis package, the calc library and harvest, the connection register and plan, and the calc stage with its DCR summary.
- **[[vault/handoffs/Handoffs map|Handoffs map]]** — the handoff log: the current state, and one dated record per session (from 2026-11-18).

## Project data

Real configs, SAP models and project findings are not in this repo: they live outside it (configs and models in a local project folder) and in the Notes vault, with the handoffs before 2026-11-18. Nothing in this repo may identify a project, the handoffs in `vault/handoffs/` included.
