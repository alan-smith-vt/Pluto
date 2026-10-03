---
title: Tapered beams (ELEM slot 4) and META upAxis
status: current
created: 2026-10-03
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

- **Taper.** A beam `ELEM` record's slot 4 holds `sectionIdxB + 1`.
  - `0` means a straight member: end B uses `sectionIdx`.
  - Otherwise end B uses section `slot4 − 1`.
  - Schema: [[vault/format/v4-schema|v4-schema]] §4.2.
- **Up axis.** META may carry `upAxis`, `"Y"` or `"Z"`: the model's vertical axis as its solver defines it.
  - The viewer applies it at load but never saves it over the user's Z-up preference.
  - Schema: §5.

## Why

- **Reducers.** Pipe-stress models (CAESAR II) have concentric and eccentric reducers. Valves are drawn as two tapered halves. The binary had one section per beam, so a reducer drew at one size.
- **Alternatives rejected:**
  - Stepped short beams: extra nodes, interpolated results, and a stepped look.
  - A constant OD: the wrong picture.
- **Why slot 4.** It was reserved and every writer wrote 0. So:
  - every existing file is a straight-member file under the new rule;
  - a viewer that ignores the slot draws end A's section;
  - the reader needed no change.
- **Hash.** A tapered member changes `geometryHash`, since `ELEM` is hashed. A straight member keeps the old bytes.
- **Up axis.** CAESAR II models are Y-up, while the plant and SAP files are Z-up. Until now the viewer's Z-up toggle was a per-browser setting that knew nothing about the file.

## Where it lives

- **Writer:**
  - `RawViewerWriter.BeamMember.SectionIndexB`: -1 = straight. Equal to `SectionIndex` is also written as straight.
  - `RawViewerWriter.UpAxis`: null = not emitted.
  - Proven byte-identical to the previous writer for shell-only, shell + beam, and beam-only exports, both geometry-only and with results.
- **Viewer:**
  - `beamGeometry.js` builds ring B from the end-B section when both outlines have the same point count, and otherwise falls back to end A.
  - The readout shows `A → B`.
  - `model.upAxis` is applied by `applyFileUpAxis()`.
  - Test: `viewer/tests/test_beamtaper.js`.
