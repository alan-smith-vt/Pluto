---
title: CAESAR II neutral file, loads, restraint codes and bend radius codes
status: draft
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR II neutral file: loads, restraint codes and bend radius codes

How the `.cii` holds applied loads (forces and moments, imposed displacements, uniform loads, wind), CAESAR's restraint type codes, and the codes CAESAR writes for a bend radius chosen by name, as Pluto reads and writes them (`CaesarPipeImport`, `CaesarNeutralWriter`, [[vault/format/pipe-model|pipe-model]]).

**Sources:**
- The neutral-file chapter of CAESAR II's user's guide, supplied by the user (2026-10-22). It is summarised here in our own words, never copied.
- CAESAR-written files: the 15.01 fixture, and public 11.00 jobs kept outside the repo.
- A small 15.01 job the user built and exported from CAESAR with forces, uniform loads and wind, and CAESAR's input screens for two of its elements (2026-10-22, kept outside the repo).
- Three probe jobs Pluto wrote, imported into CAESAR 15.01, read on its input lists and exported again (2026-10-24, kept outside the repo): [[#The probe jobs (2026-10-24)|below]].

| Item | Documentation | Seen in a CAESAR file |
|---|---|---|
| DISPLMNT | yes | yes: 11.00 jobs (layout and the 9999.99 mark), and a 15.01 probe (15.01's number format, the unused slot) |
| FORCMNT | yes | yes, a 15.01 export (layout, and the unused slot as node 0 and zeros) |
| UNIFORM | yes | yes, a 15.01 export (the g flags). Carried forward to later elements: CAESAR's user guide and error checker |
| WIND | yes | yes: wind in a 15.01 export (type 0, shape factor), wave in a 15.01 probe (type 1 and all five values). Carried forward like uniform loads |
| Restraint codes 1–62 | yes | 1–26, every type the builder writes (a 15.01 probe) |
| Skewed restraints (an axis type with cosines) | no | yes, a 15.01 probe (X with stiffness and gap, one-way +X, RX) |

**The check against CAESAR (2026-10-22).** Pluto read the export and wrote it back byte for byte. Then the same loads were written as new blocks, with no CAESAR text to copy:
- UNIFORM, WIND and CONTROL came out line for line as CAESAR wrote them.
- FORCMNT held the same blocks, in element order. CAESAR had put the last element's block first; the pointers say which block is whose, so the order does not matter.

Whatever no CAESAR file has shown yet is written with a `[notice ]` line from `Build-Pipe.ps1` (`CaesarNeutralWriter.Notices`). Since the probe jobs, that is only a restraint code beyond 26 (rods, bilinear supports, springs, one-way snubbers), which the builder never writes and a workbook row can name. Its first CAESAR import confirms it: re-export from CAESAR and run `-Compare`.

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

- **An unused second slot:** node 0, then 9 lines. For displacements 11.00 jobs fill those lines with 9999.99, and so did CAESAR 15.01 with the probe's; the writer does the same. For forces CAESAR 15.01 writes 0, and so does the writer.
- **Ownership:** the entry belongs to an element that has the node (FROM, TO or a bend node). A displacement may also sit on a node only a CNODE names. An imported entry stays in its block and slot.
- **The model:** `PipeModelNodeLoad`: `Kind` (force | displacement), `Node`, `Vectors[9][6]`.
  - A free displacement DOF is NaN.
  - An imposed 0 holds the DOF in that vector. CAESAR's Displacements list shows it as 0.0000 and a free DOF as blank.
  - A force read as 9999.99 is NaN and acts as 0.

## UNIFORM: uniform loads per length or in g's

One block is **6 lines, 36 values in order**: 9 vectors × (UX, UY, UZ, g flag). The g flag is 1 for g's and 0 for force per length.

| Line | Values |
|---|---|
| 1 | U1x U1y U1z g1 U2x U2y |
| 2 | U2z g2 U3x U3y U3z g3 |
| 3–6 | the same pattern through U9 |

- **A block carries forward.** It acts on its element and on every later element (input order) until another block changes it; a block of zeros ends it. CAESAR's user guide says so for the Uniform Loads tab, and its error checker notes it (166E: four consecutive elements with uniform loads defined, "you do not need to do this"). The element's Uniform Loads box marks where a load is defined, not where it acts. Decision: [[vault/decisions/2026-10-24-uniform-wind-carry-forward|2026-10-24-uniform-wind-carry-forward]].
  - **Corrected 2026-10-24.** The 15.01 export of 2026-10-22 had blocks on five elements and none on the sixth, whose box was unchecked. That was read as "one block per element, no carry-forward"; in CAESAR the sixth element carries the fifth's load.
- **How Pluto reads it:** an element without a block gets a copy of the load before it, so the model holds what acts on each element (`PipeModelElement.Uniform`, null = none; a carried copy has no raw lines).
- **How Pluto writes it:** a block where the load starts or changes, a zero block where it stops. An imported job's own blocks stay where they were while unedited; an edited one that only repeats the load before it is dropped. CAESAR 15.01 imported a probe ending a load this way with no error or note (2026-10-24).
- **The g flags:** one per vector, and CAESAR's error checker reads every one, used or not:
  - a block holding both kinds warns for its element (246E): a block of g loads with its unused vectors at 0 did;
  - a vector with both kinds across blocks warns for the vector (247E): a probe with U1 in g's on part of a line and per length on the rest warned for every vector whose flags differed between blocks.
  - So a new or edited block gives each unused vector the model's flag for it (`CaesarNeutralWriter.UniformFlags`). A model of one kind gets that kind on every flag and no warning; a model that mixes them gets the `uniform-mixed` check, as CAESAR will warn.
- **The global switch:** line 2 of MISCEL_1's execution options starts with a "uniform loads in g's" switch (0 or 1): the default for each vector in CAESAR's input. It is carried as written; the per-vector flag is what the model edits.

## WIND: wind and wave data

One block is **1 line, 6 values**:

| Value | Holds |
|---|---|
| 1 | type: 0 wind, 1 wave, 2 off |
| 2 | wind shape factor (wave: drag coefficient) |
| 3–6 | wave only: added-mass coefficient, lift coefficient, marine growth, its density |

- **Carried forward like uniform loads** (`PipeModelElement.Wind`, null = none): CAESAR's user guide says the shape factor applies to every later element, and a 0 disables it. Pluto ends wind with a type 2 (off) block where it stops.
- **Wave (type 1):** CAESAR's Wind / Wave list shows the five values as drag coefficient, added-mass coefficient, lift coefficient, marine growth (a length) and its density, in the job's units. Pluto keeps them as written.
- Type 2 (off) is documented; a shape factor of 0 also means no wind. CAESAR 15.01 accepted an off block ending wind (a probe, 2026-10-24).
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
- **What the builder and the workbook write:** the plain types, codes 1–26 (`PipeModelCodes.BuildTypes`), all confirmed by CAESAR. Rods, bilinear supports and springs need data the restraint record does not hold, so they are read and carried but not built.
- **Skewed restraints:** an axis type (X, +X, RX …) whose cosines give the direction. CAESAR shows the cosines beside the type and keeps the stiffness and gap.

## Bend radius codes

A bend's radius (BEND, value 1) is a plain length, or a **negative code**, a multiple of −1.0101: a radius chosen by name in CAESAR's input, which follows the pipe size. The documentation does not mention them.

**Confirmed with CAESAR 15.01 (2026-10-23).** Two probe jobs, each with one 90° bend per code on 6 in pipe, were imported into CAESAR, read on its bend list, and exported again:

| Code | CAESAR shows | Radius | Written back as |
|---|---|---|---|
| −1.0101 | Long | 1.5 × nominal | the plain radius (9.0) |
| −2.0202 | 0.000 | none | −2.0202 |
| −3.0303 | 3D | 3 × nominal | −3.0303 |
| −4.0404 | Short | 1 × nominal | −4.0404 |
| −5.0505 | 5D | 5 × nominal | −5.0505 |
| −6.0606 … −9.0909 | 0.000 | none | as written |

- **CAESAR names plain radii too:** it shows a plain radius equal to 1.5 × nominal as "Long" (the 9.0 control bend). Its own files hold Long as a number, and 3D, Short and 5D as codes.
- **How Pluto handles them:**
  - **Kept as written**, so CAESAR resolves them as before.
  - **Drawn with the radius they name** (`PipeModelBend.NamedRadii`); the nominal size comes from the bend's OD, inch or metric.
  - **Compared by meaning:** a code and the plain radius it names are the same bend.
  - **No-radius codes** (2, 6–9) are drawn as corners, and the reader warns about them.
- **The probe's re-exports differ from it in one other way:** CAESAR fills in its default insulation density (0.0067 lb/in³) where the field was blank. It changes nothing without insulation.

## The probe jobs (2026-10-24)

Three jobs Pluto wrote from workbooks, each holding one of every unconfirmed item. Each was imported into CAESAR 15.01, read on its input lists and exported again. `-Compare` against the re-export found only the insulation density default (below), and the sections under test came back byte for byte.

| Probe | Held | CAESAR showed |
|---|---|---|
| Restraint codes | codes 10–13 and 16–26, one per node, between two anchors | XSNB YSNB ZSNB, +X, −X −Y −Z, +RX +RY +RZ, −RX −RY −RZ, +LIM −LIM at their nodes |
| Skewed restraints and bend nodes | a skewed X with stiffness 50000 and gap 0.125, a skewed one-way +X (code 13), a skewed RX; a bend with its near node at angle 0 and a mid node | each type with its cosines, stiffness and gap; the bend's nodes as written |
| Displacements and wave | D1, D2, D3 and D9 at three nodes (an imposed 0 among them), two entries in one block and one alone; wave on two elements, wind on a third | each value in its vector and DOF, 0.0000 apart from the free ones; Wave with drag, added mass, lift, marine growth and its density; Wind with its shape factor |

- **The one difference:** CAESAR fills in its default insulation density (0.0067 lb/in³) where the field was blank, as with the bend-radius probes. It changes nothing without insulation.
- **What it settles:** Pluto writes no notice for any of them now (`CaesarNeutralWriter.NoticeItems`).

## Other sections the documentation describes (carried as written)

- **ALLOWBLS:** 28 lines per block (code allowables, fatigue curves, code-specific items).
- **FLANGES:** 12 lines; line 4 is the class name.
- **EQUIPMNT:** 2 × 17 values on 6 lines: node, load limits, reference axis, rating, method.
- **NODENAME:** the FROM and TO node names, 10 characters each.
- **MISCEL_1:**
  - one material number per element;
  - nozzles: 20 values on 4 lines of 6, 6, 4, 4, read line by line ([[vault/format/caesar-neutral-nozzles|caesar-neutral-nozzles]]);
  - hangers: defaults, then per-hanger data, tags, free anchors, tables ([[vault/format/caesar-neutral-hangers|caesar-neutral-hangers]]);
  - the execution options.
- **COORDS:** count, then node X Y Z in the compound-length unit.
