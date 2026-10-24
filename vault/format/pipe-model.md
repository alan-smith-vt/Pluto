---
title: Pipe model (lossless CAESAR II carry, model JSON, compare)
status: draft
created: 2026-10-22
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# Pipe model

`PipeModel` (`scripts/arms/PipeModel.cs`) is the one pipe-stress model every pipe path goes through. It is held in CAESAR II's terms:
- elements FROM–TO with deltas, bends, rigids, expansion joints and reducers, and their design data (mill tolerance, seam weld, hydrotest pressure; hot moduli, wL, cladding and refractory carried);
- restraints, SIFs and tees;
- spring hangers: designed by CAESAR, springs as given, constant-effort supports ([[vault/format/caesar-neutral-hangers|caesar-neutral-hangers]]);
- loads: forces and moments, imposed displacements, uniform loads and wind ([[vault/format/caesar-neutral-loads|caesar-neutral-loads]]);
- COORDS.

Three things become one:

| Source | Code | What it gives |
|---|---|---|
| a TOML config | `PipeBuilder` | a new model, written with the 15.01 English templates ([[vault/arms/pipe-builder\|pipe-builder]]) |
| an existing `.cii` | `CaesarPipeImport.FromCii` | the job, with everything the model does not edit carried as written |
| a `.pipe.json` | `PipeModelJson.Read` | a model saved exactly, by `Build-Pipe.ps1 -Json` |

`CaesarNeutralWriter` writes any of them back as a `.cii`; `CaesarToPluto.Export(CaesarModel …)` shows any of them in the viewer; `CaesarCompare` compares two of them by meaning. The runner for all of it is `scripts/caesar/Build-Pipe.ps1`.

**Status: verified on Linux (2026-10-22), still `draft`** until the PS 5.1 compile and a CAESAR re-import on the production machine.

## The rule: an unedited import is written back identically

Importing a `.cii` and writing it back without an edit gives the source file **byte for byte**: the same CAESAR version's layout, the same line ending, code page and trailing lines. Every import checks this on the spot (`CaesarPipeImportResult.Identical` / `ByteIdentical`) and says so in the runner's report. An edit changes only what it touches.

> [!info]- Overlay and carry, field by field
> - **Raw text is kept** wherever the file has it:
>   - every element record (the 15-line 15.01 record, or the version's own);
>   - every bend / rigid / expansion-joint / reducer block;
>   - every restraint, SIF / tee and imposed-displacement slot;
>   - every COORDS line.
> - **Overlay:** the writer starts from those lines and rewrites only the 13-character columns whose value changed (`SetReal` / `SetInt` / `SetText`). A value that compares equal keeps its original text, however CAESAR formatted it.
> - **New items** (an element, a restraint, a SIF that the file did not have) get the 15.01 templates. In a file from another CAESAR version, a new item is written only where that version's line counts match 15.01's; otherwise the writer stops with "re-export the job from CAESAR 15.01 first". It never guesses a layout.
> - **Section order, VERSION and the prelude** are the source's. PROJECT / NOTES on the VERSION lines are overlaid only when the title or notes changed.
> - **CONTROL:** the counts (elements, blocks per section, hangers) are recomputed; the nozzle count is kept.
> - **UNITS** is carried as written.
> - **MISCEL_1:** the material numbers are overlaid when the element count is unchanged. The tail is taken apart into nozzles (carried as written), the hanger section and the execution options (carried, the ambient temperature overlaid in its own column). The hangers are the model's (`Hangers`, below): each keeps its lines as written, with only changed fields rewritten, and the section is rebuilt around them. A tail whose parts do not add up is carried whole, its hangers with it, and cannot take hanger edits ([[vault/format/caesar-neutral-hangers|caesar-neutral-hangers]]).
> - **Trailing lines** after a section, and the remainder lines inside a block, are carried where they were.

> [!info]- Blocks, slots and who owns what
> | Kind | Sections | Rule |
> |---|---|---|
> | Owned by an element | BEND, RIGID, EXPJT, REDUCERS | read into the model; raw lines overlaid |
> | Carried with an element | OFFSETS, ALLOWBLS, NODENAME, FLANGES, EQUIPMNT; a load block in a layout the model does not know | not read; travel with their element as written (`CaesarNeutralWriter.CarriedSections`) |
> | Owned by an element, carried forward | UNIFORM, WIND | read into the model (`Uniform`, `Wind`) as what acts on each element: an element without a block carries the one before. Written where the load starts or changes; raw lines overlaid |
> | Tied to a node, in slots | RESTRANT (6 per block), SIF&TEES (2), DISPLMNT (2), FORCMNT (2) | each item knows its owner element and slot |
>
> - **Block numbers:** each block keeps its number in the source (`Seq`). CAESAR numbers them in its own order, not element order, so the writer orders a section's blocks by `Seq`, then element order; a new block goes after them. The pointers in the element records follow.
> - **Slots:** an item goes back to the slot it came from while its owner element still has its node; otherwise to the first element at the node with a free slot. A slot no item fills keeps the source's text (an empty slot stays as CAESAR wrote it).
> - **Loads at nodes** (FORCMNT, DISPLMNT) stay with their owner element even when it does not have the node: a displacement may sit on a node only a CNODE names. A new entry goes to an element at its node with a free slot.

> [!info]- Units
> - Values stay **as written, in the job's units.** `PipeModel.UnitFactors` holds the file's English → job factors (CAESAR's UNITS order); a new model has the English set.
> - COORDS are converted with the job's length factors: English files exactly (inches / 12 to feet); others by the file's own factors.
> - An unchanged COORDS line keeps its text (`PipeModelCoord.ImportedXyz` records what was read).

> [!info]- Joints
> `PipeModelJoint` is a node-to-node tie described per DOF, expanded to CNODE restraints only when the `.cii` is written:
> - `node`, `cnode`, `kind` (a label: coupling, hinge, slip, ball, link, custom);
> - `frame`: `pipe` (axial, shear, torsion, bending, in the pipe's own axes at the node) or `global` (X Y Z RX RY RZ);
> - `dofs`: each with a `gap` (free travel each way) and a `stiffness` after it (0 = rigid). **A DOF not listed is free.**
> - `sif` (> 0: a user SIF at the node) and `tag`.
>
> Pipe-frame DOFs need an element at the node to give the axis; the writer reports a joint without one.

> [!info]- Hangers
> `PipeModelHanger`, one per hanger node, in CAESAR's order (MISCEL_1):
> - `Node`, `CNode` (0 = to ground);
> - CAESAR's 11 values in its order: `Rate` (0 = CAESAR designs it), `LoadVariation` (%, 25), `RigidCriteria`, `Space`, `ColdLoad`, `HotLoad`, `OperatingLoad`, `MaxTravel` (9999.99 = no limit), `MultiLoad`, `HardwareWeight`, `ConstantLoad`;
> - `Kind` follows from them: `constant` (a constant load), `spring` (a rate), else `designed`;
> - `Table` (0 = the job's default), `Count` (hangers at the location; 0 = one), `ShortRange` (1 = allowed), the anchors freed while it is designed (`FreeNode1/2` with codes 1 Y, 2 XY, 3 ZY, 4 XYZ, 5 all), `Tag`, `Guid`;
> - `Raw` and `FreeRaw`: its lines as written (null = new).
>
> The job's hanger defaults (table, load variation …, the design options) stay as written; a new model gets CAESAR 15.01's.

> [!info]- Element design data
> The element record's real rows 6–9, past the densities, in CAESAR's guide (items 33–53): minus and plus mill tolerance (% of the wall), seam weld (1 / 0), hydrotest pressure; hot elastic moduli for T1–T9; the wL factor; two element orientation angles (FEATools data, carried as `TeeAngle1/2`); cladding thickness and density, insulation + cladding weight per length, refractory thickness and density.
> - The model holds them all (`PipeModelElement.Design()`); the workbook and the TOML edit mill tolerance, seam weld and hydro pressure. The rest are carried.
> - **Evidence for the positions:** the guide; the CAESAR 15.01 fixture (zeros there); public CAESAR 11.00 jobs whose item 36 is a hydrotest pressure by its value (1.5 × P1 in one, above P1 in another). CAESAR importing a value Pluto wrote there is in the combined probe.
> - A record with fewer rows (an older CAESAR) cannot take a value in a missing row: the writer says so.

> [!info]- Seeds
> A new model can take a CAESAR job's settings (`Build-Pipe.ps1 -Seed <job.cii>`, `CaesarPipeImport.ApplySeed`):
> - the VERSION lines;
> - UNITS. A TOML model is English, so it takes only an English seed;
> - the 4 execution-option lines of MISCEL_1;
> - the first ALLOWBLS block.

## The model JSON (`pluto-pipe-model`, version 2)

`PipeModelJson.Write` / `Read`, written by `Build-Pipe.ps1 -Json` as `<base>.pipe.json`. It is the internal base model: the Excel workbook (Stage B) keeps one to diff edits against. It holds the whole model, carried text included, so `.cii` → JSON → `.cii` is identical too.

- **Exact:** numbers are written with 17 significant digits and parsed back to the same double. A hand-written writer and a strict reader, not `JavaScriptSerializer` (which turns fractions into decimals and caps the text at 2 MB).
- **Deterministic:** keys in a fixed order; dictionaries sorted by key. The same model always gives the same text.
- **NaN is `null`** (a free displacement DOF).
- **Version 2** (Stage C) adds the loads. A version 1 file, whose displacements were carried as slot lines (`nodeItems`), still reads.

> [!info]- Keys
> - **Top level:** `format`, `version`, `title`, `notes`, `zUp`, `ambient`, `code`, `unitFactors`, `allowablesBlock`, `source`, `elements`, `restraints`, `joints`, `sifs`, `hangers` (only when there are any), `nodeLoads`, `coords`.
> - **`source`** (imported or seeded models only): `fileName`, `version`, `versionLine`, `codePage`, `lineEnding`, `finalNewline`, `seed`, `prelude`, `sectionOrder`, `headers`, `sections` (raw lines per section), `linesPerBlock`, `trailing`, `miscelMaterialLines`, `miscelTail`, `tailNozzleLines` / `tailHangerLines` / `tailHangers` (the tail's parts; absent = carried whole), `coordsCountLine`, `originalTitle`, `originalNotes`, `elementCount`.
> - **`elements[]`:** `uid`, `from`, `to`, `d` [dx dy dz], `od`, `wall`, `insul`, `corr`, `t` [T1–T9], `p` [P1–P9], `e`, `poisson`, `pipeDensity`, `insulDensity`, `fluidDensity`, `material`, `teeAngles` (CAESAR's element orientation angles), `design` (only when any is set: the 19 values `PipeModelElement.Design()` lists, CAESAR's items 33–46 and 49–53; a model saved without it reads them from its record), `name`, `line`, `kind`; optional `bend` {`radius`, `type`, `nodes`, `miter`, `fittingThk`, `k`, `seq`, `raw`}, `rigid` {`weight`, `type`, `seq`, `raw`}, `expJoint` {`axial`, `transverse`, `bending`, `torsion`, `effId`, `seq`, `raw`}, `reducer` {`od2`, `wall2`, `alpha`, `r1`, `r2`, `seq`, `raw`}, `uniform` {`vectors` [9 × [ux, uy, uz]], `g` [9 flags], `seq`, `raw`}, `wind` {`type`, `shape`, `addedMass`, `lift`, `marineGrowth`, `marineGrowthDensity`, `seq`, `raw`}; `raw` (the record); `carried` and `slotBlocks` {section: {`seq`, `lines`}}.
> - **`restraints[]`:** `node`, `type`, `code`, `k`, `gap`, `mu`, `cnode`, `cos`, `tag`, `guid`, `source`, `owner` (element uid), `slot`, `raw`.
> - **`joints[]`:** `uid`, `kind`, `node`, `cnode`, `frame`, `dofs` [{`dof`, `gap`, `k`}], `sif`, `tag`.
> - **`sifs[]`:** `node`, `type`, `sifIn`, `sifOut`, `surfaceNode`, `owner`, `slot`, `raw`.
> - **`hangers[]`:** `node`, `cnode`, `data` [the 11 values], `tag`, `guid`, `free` [node 1, node 2, code 1, code 2], `count`, `table`, `shortRange`, `source`, `raw`, `freeRaw`.
> - **`nodeLoads[]`:** `kind` (force | displacement), `node`, `vectors` [9 × 6, `null` = free], `source`, `owner`, `slot`, `raw`.
> - **`coords[]`:** `node`, `x`, `y`, `z` (in), `raw`, `imported`.
>
> Element `uid`s are stable ids (1..n on import or build) that restraints, SIFs and node items name as their `owner`. `raw` is null for anything new.

## Compare by meaning

`CaesarCompare.Compare(a, nameA, b, nameB, lengthTol)`, or `Build-Pipe.ps1 -In x -Compare y` with any two of `.cii` / `.toml` / `.pipe.json`. It is meant for a job against CAESAR's own re-export of it, where the text differs and the model should not.

- **Elements** matched by FROM–TO: added, removed, and every changed field.
- **Node positions** by CAESAR's own geometry rules (`CaesarGeometry`), within `lengthTol`.
- **Restraints** as a multiset per node: rigid stiffness normalized, CAESAR's default cosines filled in.
- **Loads:** forces and displacements per node and vector, as a multiset. Uniform loads and wind on each matched element.
- **SIFs / tees**, **carried blocks** (by section and owner), and **settings** (title, ambient, Z-up, units).

The result lists each kind, a one-line `Summary()` and `ToJson()`.

## Verification (2026-10-22, Linux)

- **Strict compile** of the whole batch (35 files): mono, C# 5, against the .NET Framework 4.8 reference assemblies. 0 errors, no new warnings.
- **Unchanged outputs:** the TOML builder's `.cii` and `.nodes.csv` for the example configs, and `Run-Caesar.ps1`'s `.bin` + sidecar on the fixture, are identical to the files made before this stage.
- **Lossless import:** the generic fixture, and 20 of 22 public sample neutral files (CAESAR 7.00, 11.00 and 15.01; kept outside the repo), come back byte for byte. The other two are expected: one with doubled carriage returns comes back line for line, its line endings normalized (reported), one deliberately truncated file is reported, not crashed on.
- **JSON:** `.cii` → JSON → `.cii` exact for 8 models.
- **Carry under edits:** a synthetic metric job with FORCMNT, UNIFORM, WIND, OFFSETS, two ALLOWBLS blocks, node names, a hanger and an extra text row, edited elsewhere: every carried block and the units come back unchanged; only the edited columns change.
- **Compare:** an identical pair gives no differences; planted edits are each reported; a builder model against its own `.cii` read back gives none.
- **Builder harnesses:** the three-config round trip and the 32 failure cases pass.
- **Runner under pwsh:** TOML → `.cii` / JSON / preview, the `.cii` report, a rewritten copy byte-identical, `-Compare`, `-Seed`, `.pipe.json` in, and the refusals (overwrite, output in the repo, argument errors).
- **Viewer tests** all pass.
- **Loads (Stage C, the same day):** see the pipe builder's load verification ([[vault/arms/pipe-builder|pipe-builder]]). Every check above still passes with the loads read, and the 11.00 samples' displacements come back byte for byte.
