---
title: SAP2000 frame models get their own workbook, and the model is the .s2k itself, typed
status: current
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

- **A second workbook, separate from the CAESAR one,** assembles SAP2000 models of piping, ducting and structural
  steel as frames, with SAP's links, springs and releases standing in for pipe-stress supports. It works like the
  CAESAR pipe workbook: Excel to edit, a live preview in the viewer on every save, a launcher that writes the model,
  Refresh that keeps typed cells.
- **The final model is an `.s2k` written by Pluto**, so no SAP is needed to write it. An optional `-Open` imports it
  in SAP through the one SAP controller (`scripts/sap/`, its own process) and saves the `.sdb`.
- **The round-trip source is an `.s2k` exported from SAP.**
- **Seeding the model from a CAESAR model is a later phase.** The frame model carries Kind, Line, Process and pipe
  sizes so that mapping is direct. Begun 2026-11-12: [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]].
- **Units are the model's own**, SAP's unit set for the file. Workbook headers show them, and a header in another unit
  converts its column.
- **The model is the `.s2k`, typed.** Every object owns the rows that describe it in every table keyed by its label,
  and every value is held as the file's text ([[vault/format/frame-model|frame-model]]).
- **Writing is overlay and carry,** as for CAESAR ([[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]]):
  - an unedited model is the source byte for byte;
  - an edit rewrites only its fields;
  - new rows are complete, in SAP's field order;
  - rows nobody owns are carried, follow renames, and refuse a deletion they depend on.
- **A field is written only once SAP has confirmed it.** SAP has to export the field again after importing it; a file
  SAP merely opened proves nothing. The SAP probe does this ([[vault/arms/frame-workbook|frame-workbook]]).
- **Generic workbook machinery moves into a shared kit** (`scripts/arms/workbook/Wb*`). The gate is that every
  CAESAR workbook stays byte-identical. Copying it was considered and not chosen: the copies would drift.

## Why

- **Holding text instead of re-encoded values makes losslessness free.** A value never edited cannot change, because
  it is never formatted. Tables Pluto does not understand ride along with the object they belong to, so a renamed
  frame keeps its design overwrites and auto-mesh settings, and a deleted one takes them away.
- **Typed accessors over the text** give the workbook and the viewer numbers, without a second copy that could
  disagree with the file.
- **The probe turns SAP's silent ignoring into a list.** The tank builder's `StiffTransform` showed the cost of finding
  out late ([[vault/arms/beam-offset-study|beam-offset-study]]).
- **Considered and not chosen:**
  - a fully typed model with a regenerating writer: it would lose every table and field it did not know, and reorder
    SAP's text;
  - building the model in SAP through the OAPI: SAP would be needed for every write, it is hard to diff, and the OAPI
    is not on every machine.

## Status

- **2026-11-05:**
  - the probe (`FrameProbe`, `Build-Frame.ps1 -Probe` / `-ProbeCheck`);
  - `S2kDoc`, `FrameModel`, `FrameS2k`, `FrameModelJson`;
  - the runner's `-In`, `-S2k`, `-Json`.

  Byte-for-byte round trips, and an edit harness in which each edit changes only its own lines, are recorded in
  [[vault/format/frame-model|frame-model]]. The probe results from SAP are pending.
- **2026-11-05, later:** the viewer (Phase 2); the shared kit `scripts/arms/workbook/Wb*`, extracted with every CAESAR
  workbook byte-identical (Phase 0b, [[vault/arms/workbook-kit|workbook-kit]]); the frame workbook and the runner's
  workbook switches (Phase 3a, [[vault/arms/frame-workbook|frame-workbook]]). Tables SAP has not confirmed are written
  with a notice until the probe's results come back.
