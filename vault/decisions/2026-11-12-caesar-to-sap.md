---
title: A CAESAR model seeds a SAP2000 check model of the pipe
status: current
created: 2026-11-12
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

`Build-Frame -In <job.cii | .toml | .pipe.json | CAESAR workbook>` seeds a SAP2000 frame model from the CAESAR model
(`PipeToFrame`). The model has the same pipe, loads and supports, carried as frames, links and loads, for **member
forces and support reactions for structural design**. CAESAR models are pipes with special restraint, load and element
types, and each becomes its SAP equivalent. No code checks are made: SIFs, allowables and code stresses stay in CAESAR.
This is the check model the 2026-10-22 decision named ([[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]]).

The mapping is in [[vault/format/pipe-to-frame|pipe-to-frame]]. The choices made with the user:

| Question | Chosen |
|---|---|
| Material data (the `.cii` has no E, density or alpha for a database material) | **A Materials table in the CAESAR workbook**, filled in by the user. A model with none (a `.cii` read directly, a TOML build) takes Pluto's default set by material class, with a warning. |
| Thermal | **A temperature load Tn − ambient**, with one alpha per material. |
| Seeding | **Once.** Edits go on in the frame workbook. |
| Order | **Phase 1, the linear model.** Nonlinear supports are refused, each named. **Phase 2** adds one-way supports, gaps and friction as SAP's nonlinear links. Each phase ends with a probe round: one job in CAESAR and SAP, compared in the session. |

> [!info]- Why
> - **One model, two programs.** The frame model already writes SAP's `.s2k`, the frame workbook, the preview and STAAD.
>   Seeding it means the check model gets every one of those for free, and edits go on in a workbook the user knows.
> - **Only the bends are approximate.** A pipe is a frame, a support a restraint or a link, a load a load. A bend is chords
>   with its flexibility factor, which sets the thermal reactions, and its weight kept to the arc's.
> - **Refused, not guessed.** A support the model cannot hold yet is refused by name. Leaving it out would change the
>   reactions, which are the point of the model.

> [!info]- Considered and not chosen
> - **Alpha per temperature** (a strain load per frame, exact for any alpha(T)): the user chose temperatures, Tn − ambient,
>   with one alpha per material. A thermal strain given in CAESAR's own form (a T under its alpha tolerance) stays
>   exact: it is written as strain / alpha.
> - **Re-seeding that keeps SAP-side additions** (support steel added after the seed): not now. Seed once.
> - **Pluto computing WRC 297 / API 650 nozzle flexibilities, or designing hangers:** CAESAR's numbers go in instead.

## History

- **2026-11-12, phase 1:**
  - the Materials sheet;
  - the linear seed;
  - Build-Frame's CAESAR inputs;
  - the probe job `sap-probe-1.toml`. Its CAESAR and SAP results are to come from the production machine.
