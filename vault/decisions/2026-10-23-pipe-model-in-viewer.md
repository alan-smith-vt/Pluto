---
title: The viewer shows the whole pipe model before CAESAR sees it
status: current
created: 2026-10-23
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

The viewer is the confirmation step of the pipe workflow ([[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]]).
So the preview shows everything the model holds, not only its geometry and supports (Stage D):
- **Applied loads, element properties and the build's checks are sidecar sections**, written by the CAESAR
  export from the pipe model: `loads`, `properties`, `review`, and `model.source` for where the model came
  from. They sit beside `supports`, which follows the same idea
  ([[vault/decisions/2026-10-03-imported-restraints|2026-10-03-imported-restraints]]).
- **The exporter owns them.** A re-export replaces them, so they always describe the model that was built.
  The viewer never edits them: the workbook or the TOML is the only place a model changes.
- **The same export serves every CAESAR job.** `Build-Pipe.ps1 -Preview` passes its model, source and build
  messages (`CaesarPreview`). `Run-Caesar.ps1` gets the same sections from the model imported from the `.cii`.
- **Checks carry their place.** Each check names the nodes, elements and beams it is about, so the viewer can
  fly to it. The checks are the model's (`PipeReview`), the layouts written from CAESAR's documentation
  (`NoticeItems`), and the build's own warnings.
- **Where a value applies is a hidden group.** Temperatures and pressures per vector, materials, insulation,
  fluid and wind shape factor each get a group, unticked until wanted.
- **Load cases stay CAESAR's.** The model holds load vectors (F1–F9, D1–D9, U1–U9) and wind shape factors.
  The viewer draws the vectors and does not invent cases or a wind direction.

Format: [[vault/format/features-sidecar|features-sidecar]] (the pipe model sections). Viewer:
[[vault/viewer/Viewer overview|Viewer overview]].

## Why

- **A confirmation step must show what is about to be exported.** Geometry alone hides the mistakes that cost a
  CAESAR round trip: a load on the wrong node or vector, a temperature missing on a run, a branch without a
  tee, a piece nothing holds.
- **Sidecar, not binary.** The binary holds geometry and results, written once. Loads and properties are input
  that changes with every save of the workbook, and other programs (the SAP check model, Stage E) will read
  them. That is what the sidecar is for.
- **Owned, not merged.** The groups and supports keep the user's colours and visibility across a re-export.
  The loads, properties and checks have nothing the user sets, and a stale copy would mislead.

## Not chosen

- **Loads as binary components.** They are not results and have no load case. Writing them per beam end would
  also lose the node / vector / run structure the readout needs.
- **Checks only in the runner's console.** The console lists them, but without the place each one is about.
