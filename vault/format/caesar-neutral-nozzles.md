---
title: CAESAR II neutral file - flexible nozzles (MISCEL_1)
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR II neutral file: flexible nozzles

A flexible nozzle tells CAESAR to add the flexibility of a nozzle's connection to a vessel or a tank between the nozzle node and the vessel node (or ground, a rigid vessel, without one). The nozzles sit in MISCEL_1 after the material numbers and before the hangers; CONTROL line 1 field 2 counts them. Pluto reads them into the pipe model (`PipeModel.Nozzles`), so the workbook's Nozzles sheet and TOML `[[nozzles]]` make and edit them. Layout in our own words, from CAESAR II's user guide, CAESAR-written files and CAESAR 15.01's own input panels and re-export of a probe job.

> [!info]- The layout
> 20 values per nozzle, reals, on 4 lines of 6, 6, 4 and 4: CAESAR 15.01 and 11.00 write them so. **CAESAR reads them line by line:** a line's values past its count (6, 6, 4, 4) are not read, and the next line starts the next item. Items 1–3 are the same for every type: the nozzle node, the vessel (tank) node (0 = none), the type (0 WRC 297, 1 API 650, 2 PD 5500, 3 custom). The rest by type:
>
> | Item | WRC 297 | API 650 | PD 5500 | Custom |
> |---|---|---|---|---|
> | 4 | nozzle OD | nozzle OD | vessel type (0 cylinder, 1 sphere) | nozzle OD |
> | 5 | nozzle wall | nozzle wall | nozzle OD | nozzle wall |
> | 6, 7 | vessel OD, wall | tank OD, wall | vessel OD, wall | vessel OD, wall |
> | 8 | pad | — | pad | pad |
> | 9 | — | reinforcing on the shell (1) or the nozzle (2) | — | — |
> | 10, 11 | distances to the stiffeners or heads either side | nozzle height, fluid height | as WRC 297 | — |
> | 12–14 | vessel centreline direction | displacement vector (1–9), fluid SG, tank alpha | vessel centreline direction | vessel centreline direction |
> | 15, 16 | vessel temperature, material | tank ΔT, tank E | vessel temperature, material | —, vessel material |
> | 17–20 | — | — | — | axial, in-plane, out-of-plane, torsional stiffness |
>
> PD 5500 has no nozzle wall. A custom nozzle's OD, wall and vessel data are for information only (CAESAR's panel says so); its stiffnesses are what it uses. 9999.99 is CAESAR's "infinite or undefined".
>
> **Where the guide misled:** it put PD 5500's vessel type at item 5 and the custom stiffnesses at 19–22, in 22 values. CAESAR 15.01 stopped on a PD 5500 nozzle written so (135E: the nozzle OD sat where it reads the vessel type) and showed the custom stiffnesses only because they were on the fourth line.

> [!info]- How Pluto reads and writes them
> - **Reading** (`CaesarNeutralReader`, `CaesarPipeImport`): with the hangers, when the MISCEL_1 tail's parts add up; line by line, as CAESAR reads them; each nozzle keeps its lines as written. A tail that does not add up is carried whole, and its nozzles and hangers cannot be edited.
> - **Writing** (`CaesarNeutralWriter.WriteNozzles`): a nozzle from a file over its own lines, only the values CAESAR reads on each rewritten (a line holding more keeps the rest as written). A new one in CAESAR's 4 lines of 6, 6, 4, 4.
> - **A model file saved before 2026-10-26** (22 values per nozzle) reads into today's items (`PipeModelNozzle.SetFromOldValues`): a nozzle with lines by what CAESAR reads of them; a new one by the guide's numbering, PD 5500's OD and vessel type swapped and the custom stiffnesses moved to 17–20. A workbook from then reads back with no edits.
> - **Compare** matches nozzles by node, vessel node, type and the values the type uses.
> - **The viewer** draws a nozzle as a plate at its node (kind `nozzle`), tied to its vessel node. CAESAR draws the nozzle's pipe from its vessel node: a nozzle's pipe tied to the rest only by it sits there ([[vault/arms/caesar-to-viewer|caesar-to-viewer]]).

> [!info]- Checks
> CAESAR's message numbers are from its error checker on the combined probe (CAESAR 15.01; [[vault/arms/caesar-checks|caesar-checks]]).
> - `nozzles` (info): the nozzles by type.
> - `nozzle-restrained` (warning, 266E): a restraint, an imposed displacement or a hanger at the nozzle node; what holds the node takes what the nozzle's flexibility would.
> - `nozzle-stiffener` (error, 118E): a WRC 297 or PD 5500 nozzle whose distances to the stiffeners or heads are under its radius, or not given.
> - `nozzle-material` (79E): a custom nozzle without a vessel material (error); a WRC 297 or PD 5500 one (warning: not seen on its own).
> - `nozzle-vessel-type` (error, 135E): a PD 5500 vessel type other than 0 or 1.
> - `nozzle-api-scope` (warning, 123E): an API 650 tank under 120 ft, outside Appendix P's scope.
> - `nozzle-vector` (warning, 244E): an API 650 nozzle's growth in a displacement vector that imposed displacements use too.
> - `nozzle-axis`: a WRC 297, PD 5500 or custom nozzle with no vessel centreline (warning), or one along the nozzle (error, from the documentation; the probe's stopped on 118E first).
> - `off-model`: a nozzle node on no element.

> [!info]- What is confirmed
> - **Read and written back byte for byte (2026-10-24):** a public CAESAR 11.00 job with two API 650 nozzles (metric). Edits and an added custom nozzle read back.
> - **CAESAR 15.01 imported Pluto's nozzles (2026-10-26).** Its own calculations echoed every input of the WRC 297 nozzle (the distances 60 and 72, the vessel's wall plus pad, the nozzle's OD and wall, 90 deg between the centrelines) and of the API 650 one (tank diameter, nozzle height, OD, shell, fluid height, temperature rise, E, alpha, SG, reinforcing on the shell).
> - **Its input panels and re-export (2026-10-26)** show every type's items as in the table: the custom stiffnesses as given, the PD 5500 vessel, distances, direction, temperature and material, and the PD 5500 nozzle OD at item 5. Its re-export writes 20 values on lines of 6, 6, 4, 4.
