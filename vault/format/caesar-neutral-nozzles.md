---
title: CAESAR II neutral file - flexible nozzles (MISCEL_1)
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR II neutral file: flexible nozzles

A flexible nozzle tells CAESAR to add the flexibility of a nozzle's connection to a vessel or a tank between the nozzle node and the vessel node (or ground, a rigid vessel, without one). The nozzles sit in MISCEL_1 after the material numbers and before the hangers; CONTROL line 1 field 2 counts them. Pluto reads them into the pipe model (`PipeModel.Nozzles`), so the workbook's Nozzles sheet and TOML `[[nozzles]]` make and edit them. Layout in our own words, from CAESAR II's user guide and CAESAR-written files.

> [!info]- The layout
> 22 values per nozzle, 6 per line, reals. Items 1–3 are the same for every type: the nozzle node, the vessel (tank) node (0 = none), the type (0 WRC 297, 1 API 650, 2 PD 5500, 3 custom). The rest by type:
>
> | Item | WRC 297 | API 650 | PD 5500 | Custom |
> |---|---|---|---|---|
> | 4 | nozzle OD | nozzle OD | nozzle OD | nozzle OD |
> | 5 | nozzle wall | nozzle wall | vessel type (0 cylinder, 1 sphere) | nozzle wall |
> | 6, 7 | vessel OD, wall | tank OD, wall | vessel OD, wall | vessel OD, wall |
> | 8 | pad | — | pad | pad |
> | 9 | — | reinforcing on the shell (1) or the nozzle (2) | — | — |
> | 10, 11 | distances to the stiffeners or heads either side | nozzle height, fluid height | as WRC 297 | — |
> | 12–14 | vessel centreline direction | displacement vector (1–9), fluid SG, tank alpha | vessel centreline direction | vessel centreline direction |
> | 15, 16 | vessel temperature, material | tank ΔT, tank E | vessel temperature, material | —, vessel material |
> | 19–22 | — | — | — | axial, in-plane, out-of-plane, torsional stiffness |
>
> The rest are spares. 9999.99 is CAESAR's "infinite or undefined".

> [!info]- How Pluto reads and writes them
> - **Reading** (`CaesarPipeImport`): with the hangers, when the MISCEL_1 tail's parts add up; each nozzle keeps its lines as written. A tail that does not add up is carried whole, and its nozzles and hangers cannot be edited.
> - **Writing** (`CaesarNeutralWriter.WriteNozzles`): a nozzle from a file over its own lines, only changed fields rewritten. A new one takes the line layout of the job's own nozzles. Without any, it takes the guide's 22 values on 4 lines (6, 6, 6, 4) and a `nozzle-layout` notice: a CAESAR 11.00 job wrote 20 values (6, 6, 4, 4), so 15.01's is to be seen.
> - **Compare** matches nozzles by node, vessel node, type and the values the type uses.
> - **The viewer** draws a nozzle as a plate at its node (kind `nozzle`), tied to its vessel node.

> [!info]- Checks
> - `nozzles` (info): the nozzles by type.
> - `nozzle-restrained` (warning): a restraint to ground at the nozzle node takes what the nozzle's flexibility would.
> - `nozzle-axis`: a WRC 297, PD 5500 or custom nozzle with no vessel centreline (warning), or one along the nozzle (error; CAESAR's error checker stops on a nozzle collinear with its vessel).
> - `off-model`: a nozzle node on no element.

> [!info]- What is confirmed (2026-10-24)
> - **Read and written back byte for byte:** a public CAESAR 11.00 job with two API 650 nozzles (metric). Edits and an added custom nozzle read back.
> - **Not yet seen in CAESAR:** a nozzle Pluto wrote, and 15.01's own nozzle lines. The combined probe job asks CAESAR to import one of each type.
