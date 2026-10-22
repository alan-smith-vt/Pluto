---
title: CAESAR II neutral file, loads and restraint codes
status: draft
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR II neutral file: loads and restraint codes

How the `.cii` holds applied loads (forces and moments, imposed displacements, uniform loads, wind), and CAESAR's restraint type codes, as Pluto reads and writes them (`CaesarPipeImport`, `CaesarNeutralWriter`, [[vault/format/pipe-model|pipe-model]]).

**Sources:**
- The neutral-file chapter of CAESAR II's user's guide, supplied by the user (2026-10-22). It is summarised here in our own words, never copied.
- CAESAR-written files: the 15.01 fixture, and public 11.00 jobs kept outside the repo.
- A small 15.01 job the user built and exported from CAESAR with forces, uniform loads and wind, and CAESAR's input screens for two of its elements (2026-10-22, kept outside the repo).

| Item | Documentation | Seen in a CAESAR file |
|---|---|---|
| DISPLMNT | yes | yes, 11.00 jobs (layout and the 9999.99 mark) |
| FORCMNT | yes | yes, a 15.01 export (layout, and the unused slot as node 0 and zeros) |
| UNIFORM | yes | yes, a 15.01 export (the g flags; one block per loaded element) |
| WIND | yes | wind: yes, a 15.01 export (type 0, shape factor; one block per element). Wave data: not yet |
| Restraint codes 1–62 | yes | 1–9, 14, 15 |

**The check against CAESAR (2026-10-22).** Pluto read the export and wrote it back byte for byte. Then the same loads were written as new blocks, with no CAESAR text to copy:
- UNIFORM, WIND and CONTROL came out line for line as CAESAR wrote them.
- FORCMNT held the same blocks, in element order. CAESAR had put the last element's block first; the pointers say which block is whose, so the order does not matter.

Whatever no CAESAR file has shown yet is written with a `[notice ]` line from `Build-Pipe.ps1` (`CaesarNeutralWriter.Notices`). The first CAESAR import of a job with each kind confirms it: re-export from CAESAR and run `-Compare`.

**Load cases are not in the neutral file.** CAESAR builds them from the load vectors (W, T1, P1, F1, D1, U1, WIN1 …) and recommends a set on import.

## Shared rules

- **Number fields:** 13-character columns after a 2-character indent, 6 per line. 15.01 writes `d.ddddddE+xx`; older versions write a G format. Both read the same.
- **Blocks:** each load block belongs to the element whose pointer names it.
  - **Pointer order** in the element record: BEND, RIGID, EXPJT, RESTRANT, DISPLMNT, FORCMNT / UNIFORM, WIND, OFFSETS, ALLOWBLS, SIF&TEES, NODENAME / REDUCERS, FLANGES, EQUIPMNT.
  - CONTROL counts each section's blocks.
  - Blocks are numbered in CAESAR's own order, not element order.
- **9999.99 means "not given".** For a displacement, that DOF is free.
- **Units:** values are in the job's units. The UNITS block holds 22 conversion factors (English to job):
  - length, force, mass, moment in, moment out, stress, temperature scale and offset, pressure, E;
  - pipe, insulation and fluid density;
  - translational and rotational stiffness;
  - uniform load, g load, wind;
  - elevation, compound length, diameter, wall.

  Forces use the force factor, moments the moment-input factor, and displacements the length factor (rotations are degrees). Uniform loads use the uniform-load factor, or none when in g's.

## FORCMNT and DISPLMNT: loads at nodes

One block holds **two entries** (slots), 10 lines each, so 20 lines per block:

| Line | Holds |
|---|---|
| 1 | the node number, alone |
| 2–10 | one load vector per line: FX FY FZ MX MY MZ (forces), DX DY DZ RX RY RZ (displacements); vectors 1–9 are CAESAR's F1…F9 / D1…D9 |

- **An unused second slot:** node 0, then 9 lines. 11.00 jobs fill those lines with 9999.99 for displacements; the writer does the same. For forces CAESAR 15.01 writes 0, and so does the writer.
- **Ownership:** the entry belongs to an element that has the node (FROM, TO or a bend node). A displacement may also sit on a node only a CNODE names. An imported entry stays in its block and slot.
- **The model:** `PipeModelNodeLoad`: `Kind` (force | displacement), `Node`, `Vectors[9][6]`.
  - A free displacement DOF is NaN.
  - An imposed 0 holds the DOF in that vector.
  - A force read as 9999.99 is NaN and acts as 0.

## UNIFORM: uniform loads per length or in g's

One block is **6 lines, 36 values in order**: 9 vectors × (UX, UY, UZ, g flag). The g flag is 1 for g's and 0 for force per length.

| Line | Values |
|---|---|
| 1 | U1x U1y U1z g1 U2x U2y |
| 2 | U2z g2 U3x U3y U3z g3 |
| 3–6 | the same pattern through U9 |

- **One block per loaded element.** Every element the load acts on has its own block, with the same values when a whole line is loaded. An element without a block has none: **the file does not carry loads forward.**
  - The 15.01 export shows it. Four vectors on five elements were written as five identical blocks. The sixth element had no block, and CAESAR's input screen shows its Uniform Loads box unchecked.
  - The model keeps each element's own load (`PipeModelElement.Uniform`, null = none).
- **Removing a load:** the element's block goes (its pointer is 0).
- **The global switch:** line 2 of MISCEL_1's execution options starts with a "uniform loads in g's" switch (0 or 1). It is carried as written; the per-vector flag is what the model edits.

## WIND: wind and wave data

One block is **1 line, 6 values**:

| Value | Holds |
|---|---|
| 1 | type: 0 wind, 1 wave, 2 off |
| 2 | wind shape factor (wave: drag coefficient) |
| 3–6 | wave only: added-mass coefficient, lift coefficient, marine growth, its density |

- **One block per element**, like uniform loads (`PipeModelElement.Wind`, null = none). In the 15.01 export, wind on four of six elements gave four blocks. The two without had none, and CAESAR's input screen shows their Wind / Wave box unchecked.
- Type 2 (off) is documented. Pluto writes no block instead, as CAESAR did.
- The wind speed or pressure profile is in CAESAR's load-case setup, not in the neutral file.

## OFFSETS (not a load)

One line: the FROM node's offset X Y Z, then the TO node's. It is carried as written.

## Restraint type codes

CAESAR's table for the RESTRANT type field:

| Codes | Types |
|---|---|
| 1–9 | ANC, X, Y, Z, RX, RY, RZ, GUI, LIM |
| 10–12 | XSNB, YSNB, ZSNB (snubbers) |
| 13–18 | +X, +Y, +Z, −X, −Y, −Z |
| 19–24 | +RX, +RY, +RZ, −RX, −RY, −RZ |
| 25–26 | +LIM, −LIM |
| 27–35 | XROD, YROD, ZROD, +XROD, +YROD, +ZROD, −XROD, −YROD, −ZROD (large-rotation rods) |
| 36–41 | X2, Y2, Z2, RX2, RY2, RZ2 (bilinear) |
| 42–47 | +X2, +Y2, +Z2, −X2, −Y2, −Z2 |
| 48–53 | +RX2, +RY2, +RZ2, −RX2, −RY2, −RZ2 |
| 54–56 | XSPR, YSPR, ZSPR |
| 57–62 | +XSNB, +YSNB, +ZSNB, −XSNB, −YSNB, −ZSNB |

- **Which table to trust:** the guide's database-export table lists different names from 48 on (a duplicated +RX2 run, no springs). The neutral-file table above is the one for `.cii` files.
- **What the builder and the workbook write:** the plain types, codes 1–26 (`PipeModelCodes.BuildTypes`). Rods, bilinear supports and springs need data the restraint record does not hold, so they are read and carried but not built.

## Other sections the documentation describes (carried as written)

- **ALLOWBLS:** 28 lines per block (code allowables, fatigue curves, code-specific items).
- **FLANGES:** 12 lines; line 4 is the class name.
- **EQUIPMNT:** 2 × 17 values on 6 lines: node, load limits, reference axis, rating, method.
- **NODENAME:** the FROM and TO node names, 10 characters each.
- **MISCEL_1:**
  - one material number per element;
  - nozzles: 22 values, 4 lines each;
  - hangers: defaults, then per-hanger data, tags, free anchors, tables;
  - the execution options.
- **COORDS:** count, then node X Y Z in the compound-length unit.
