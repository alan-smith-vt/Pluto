---
title: The pipe model carries a CAESAR job losslessly; the workbook will be its master
status: current
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

Pipe-stress models are defined in **an Excel workbook or a TOML config** and nowhere else. The viewer is a confirmation step, never an editor or a source. Every output comes from the one `PipeModel`:
- the `.cii` for CAESAR II;
- the viewer preview;
- later, an `.s2k` for SAP2000.

Stage A (this decision's first part) makes that model **lossless for an existing CAESAR job**:
- **Import keeps everything.** `CaesarPipeImport.FromCii` reads a `.cii` into a `PipeModel` and keeps the raw text of every record, block and slot. It also keeps the sections the model does not read: loads, wind, offsets, allowables, node names, flanges, equipment, the hangers and nozzles in MISCEL_1, and UNITS.
- **Write is overlay-and-carry.** `CaesarNeutralWriter` gives an unedited import back byte for byte. After an edit it rewrites only the changed 13-character columns. Blocks keep CAESAR's numbering, slots stay with their owner, carried blocks travel with their element.
- **Every import proves it.** Each import writes the model back in memory and reports whether the result is identical to the source.
- **The model JSON is the exact base.** `PipeModelJson` (`pluto-pipe-model` v1) stores the whole model with 17-digit numbers. The workbook will keep one hidden to diff the user's edits against.
- **Compare is by meaning.** `CaesarCompare` checks two models element by element, node by node (CAESAR's geometry), restraint by restraint, carried block by block. Closing the loop with CAESAR means comparing against CAESAR's own re-export.
- **One runner.** `Build-Pipe.ps1 -In <.toml | .cii | .pipe.json>` with `-Cii`, `-Json`, `-Preview`, `-Compare`, `-Seed`, in any combination.
- **The SAP model is a check on CAESAR.** It will have the same pipe, the same loads, and equivalent fixities, supports, restraints and releases (the user's clarification, 2026-10-22).

Format and rules: [[vault/format/pipe-model|pipe-model]]. Usage: [[vault/arms/pipe-builder|pipe-builder]].

## Why

- **An existing job must survive the round trip.** A job opened in the workbook and exported without edits must be the same job, or nothing built on it can be trusted. Regenerating the file from the model's fields would lose everything the model does not hold, and change CAESAR's formatting and block order. Carrying the text and overlaying only edits keeps the job intact by construction. The byte check makes that visible on every import.
- **No layout is guessed.** A new block in a layout the files have not shown (an imposed displacement, a new item in a pre-15.01 file) is refused with a message, not written from a guess. Loads get their own layouts from CAESAR's documentation in Stage C.
- **The viewer stays a picture.** Editing in the viewer was considered and dropped (user, 2026-10-22). Two editable sources would need a merge between them. With one source, the review loop is simply: save the workbook, reload the view.
- **Exact JSON, hand-written:** `JavaScriptSerializer` turns fractions into decimals and caps text at 2 MB. A base model that is not exact would show phantom edits.
- **Considered and not chosen:**
  - a regenerate-only writer with a list of known sections: it loses what CAESAR adds in versions not seen here;
  - storing the source `.cii` beside the model and patching it: the model would no longer be self-contained, and a moved element's blocks would not follow it.

## Status

- **Stage A (2026-10-22):** the lossless import, the overlay-and-carry writer, the model JSON, Compare and the runner switches are on branch `pipestress`. Verified on Linux: an unedited import is byte-identical for the fixture and 20 of 22 public samples; the other two are expected.
- **Next:** Stage B, the workbook; C, loads; D, the viewer as the confirmation step; E, the SAP check model.
- **Still to do on the production machine:** the PS 5.1 compile, and a real 15.01 job round-tripped and re-imported into CAESAR.
