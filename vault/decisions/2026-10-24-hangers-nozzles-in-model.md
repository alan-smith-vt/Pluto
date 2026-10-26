---
title: Hangers and nozzles are the model's
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

# Hangers and nozzles are the model's

**Decision (2026-10-24).** Spring hangers and flexible nozzles move from the carried MISCEL_1 tail into the pipe model (`PipeModel.Hangers`, `PipeModel.Nozzles`). The workbook (Hangers, Nozzles sheets) and the TOML (`[[hangers]]`, `[[nozzles]]`) make and edit them; the writer rebuilds the tail around them. Layouts: [[vault/format/caesar-neutral-hangers|caesar-neutral-hangers]], [[vault/format/caesar-neutral-nozzles|caesar-neutral-nozzles]].

## Why

- **They are what a stress model is built around.** Spring supports near hot equipment and nozzle flexibility at vessels and tanks are common inputs. Carried as written, they could only be added in CAESAR after every import.
- **The builder refused hangers** ("CAESAR designs them"). CAESAR designs only the hangers given no spring; a spring as given and a constant support are inputs, and even a designed hanger needs its node, table and freed anchors.

## What follows

- **Imported jobs stay byte-identical.** Each hanger and nozzle keeps its lines as written, with only changed fields rewritten; the lists and defaults keep theirs while their counts hold.
- **A tail Pluto cannot take apart is carried whole**, as before, and refuses hanger or nozzle edits with a message; the workbook says so on its Start sheet (and lists the tail on Carried).
- **New items are written in CAESAR 15.01's layout.** The hangers' from a 15.01 file; the nozzles' from CAESAR's re-export of the combined probe (2026-10-26): 20 values on lines of 6, 6, 4, 4, read line by line, which corrected the guide's PD 5500 and custom items. CAESAR's panels showed every hanger and nozzle of the probe as Pluto meant it, but the PD 5500 one written by the guide.
- **Older workbooks and model files** (without these sheets or keys) read as before: the base's hangers and nozzles stay.
