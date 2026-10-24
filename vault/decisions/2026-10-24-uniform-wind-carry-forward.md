---
title: Uniform loads and wind carry forward
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

# Uniform loads and wind carry forward

**Decision (2026-10-24).** CAESAR applies a UNIFORM or WIND block to its element and to every later element (input order) until another block changes it. The pipe model holds what acts on each element:
- **reading** a job, an element without a block gets a copy of the load before it;
- **writing**, a block goes where the load starts or changes, and a load ends with a zero UNIFORM block or an "off" WIND block (type 2).

Layouts: [[vault/format/caesar-neutral-loads|caesar-neutral-loads]].

## Why

- **CAESAR's user guide** says so for both tabs. Uniform loads apply to the element and to all later ones until changed. The wind shape factor applies to all later elements, and a value of 0 disables it.
- **CAESAR's error checker** says so too. A job Pluto wrote with a block on every element got note 166E: four consecutive elements with uniform loads defined, "you do not need to do this".
- **The 2026-10-22 reading was wrong.** It came from a CAESAR export whose unloaded element had no block and an unchecked Uniform Loads box. Carry-forward looks the same: the box marks where a load is defined, not where it acts. Under that reading, a workbook with a load on part of a line would have loaded the rest of the line in CAESAR.

## What follows

- **The workbook and the TOML do not change.** A row (or a run) names the elements a load acts on; an element no row names has none, and Pluto ends the load there.
- **Imported jobs stay byte-identical.** An element's own block stays where it was while unedited. An edited block that only repeats the load before it is dropped.
- **g flags:** CAESAR's error checker reads every flag, used or not: one block with both kinds warns (246E), one vector with both kinds across blocks too (247E). A new or edited UNIFORM block gives each unused vector the model's flag for it, so a model of one kind gets no warning. A model that mixes g's and force per length gets the `uniform-mixed` check.
- **Reading shows the carried load** in the viewer, in Compare and in a workbook made from the job.

## Confirmed in CAESAR (2026-10-24)

- **The blocks that end a load.** A probe ending a uniform load with a zero block and wind with an off block imported into CAESAR 15.01 with no error or note.
- **The job that had raised 48E, 246E and 166E** came back clean once its bend node was renumbered: one uniform block, every flag in g's.
