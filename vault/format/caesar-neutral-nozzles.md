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
> | 5 | nozzle wall | nozzle wall | vessel type (0 cylinder, 1 sphere): **not here**, see below | nozzle wall |
> | 6, 7 | vessel OD, wall | tank OD, wall | vessel OD, wall | vessel OD, wall |
> | 8 | pad | — | pad | pad |
> | 9 | — | reinforcing on the shell (1) or the nozzle (2) | — | — |
> | 10, 11 | distances to the stiffeners or heads either side | nozzle height, fluid height | as WRC 297 | — |
> | 12–14 | vessel centreline direction | displacement vector (1–9), fluid SG, tank alpha | vessel centreline direction | vessel centreline direction |
> | 15, 16 | vessel temperature, material | tank ΔT, tank E | vessel temperature, material | —, vessel material |
> | 19–22 | — | — | — | axial, in-plane, out-of-plane, torsional stiffness |
>
> The rest are spares. 9999.99 is CAESAR's "infinite or undefined".
>
> **PD 5500 is not laid out so.** CAESAR 15.01 stopped on the PD 5500 nozzle Pluto wrote with these items, on an invalid vessel type (135E): it was given 1 (a sphere) at item 5, so CAESAR reads the vessel type elsewhere. Pluto flags a PD 5500 nozzle it writes as an error (`nozzle-pd5500`) until a PD 5500 nozzle written by CAESAR shows the layout.

> [!info]- How Pluto reads and writes them
> - **Reading** (`CaesarPipeImport`): with the hangers, when the MISCEL_1 tail's parts add up; each nozzle keeps its lines as written. A tail that does not add up is carried whole, and its nozzles and hangers cannot be edited.
> - **Writing** (`CaesarNeutralWriter.WriteNozzles`): a nozzle from a file over its own lines, only changed fields rewritten. A new one takes the line layout of the job's own nozzles. Without any, it takes the guide's 22 values on 4 lines (6, 6, 6, 4), which CAESAR 15.01 imported (a CAESAR 11.00 job wrote 20 values, 6, 6, 4, 4). A new custom nozzle gets a `nozzle-layout` notice: its stiffnesses (items 19-22) are still to be seen read back.
> - **Compare** matches nozzles by node, vessel node, type and the values the type uses.
> - **The viewer** draws a nozzle as a plate at its node (kind `nozzle`), tied to its vessel node.

> [!info]- Checks
> CAESAR's message numbers are from its error checker on the combined probe (CAESAR 15.01; [[vault/arms/caesar-checks|caesar-checks]]).
> - `nozzles` (info): the nozzles by type.
> - `nozzle-restrained` (warning, 266E): a restraint, an imposed displacement or a hanger at the nozzle node; what holds the node takes what the nozzle's flexibility would.
> - `nozzle-stiffener` (error, 118E): a WRC 297 nozzle whose distances to the stiffeners or heads are under its radius, or not given.
> - `nozzle-material` (79E): a custom nozzle without a vessel material (error); a WRC 297 one (warning: not seen on its own).
> - `nozzle-api-scope` (warning, 123E): an API 650 tank under 120 ft, outside Appendix P's scope.
> - `nozzle-vector` (warning, 244E): an API 650 nozzle's growth in a displacement vector that imposed displacements use too.
> - `nozzle-pd5500` (error, 135E): a PD 5500 nozzle Pluto writes. A PD 5500 nozzle's values are not checked, as they are not where the documentation puts them.
> - `nozzle-axis`: a WRC 297 or custom nozzle with no vessel centreline (warning), or one along the nozzle (error, from the documentation; the probe's stopped on 118E first).
> - `off-model`: a nozzle node on no element.

> [!info]- What is confirmed
> - **Read and written back byte for byte (2026-10-24):** a public CAESAR 11.00 job with two API 650 nozzles (metric). Edits and an added custom nozzle read back.
> - **CAESAR 15.01 imported Pluto's nozzles (2026-10-26).** Its own calculations echoed every input of the WRC 297 nozzle (the distances 60 and 72, the vessel's wall plus pad, the nozzle's OD and wall, 90 deg between the centrelines) and of the API 650 one (tank diameter, nozzle height, OD, shell, fluid height, temperature rise, E, alpha, SG, reinforcing on the shell). The custom nozzle stopped on its missing material (79E); the PD 5500 one on its vessel type (135E).
> - **Still to be seen:** 15.01's own nozzle lines, the PD 5500 layout, a custom nozzle's stiffnesses read back.
