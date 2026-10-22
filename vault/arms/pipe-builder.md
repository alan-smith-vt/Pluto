---
title: Pipe builder (TOML → CAESAR II neutral file)
status: draft
created: 2026-10-07
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Pipe builder

A pipe-stress model described parametrically in a TOML config becomes a CAESAR II neutral file (`.cii`) that CAESAR II imports. It runs on the production machine: Windows PowerShell 5.1, C# 5 through `Config.ps1`, with no CAESAR II and no Python needed. This is branch `pipestress`.

The config describes:
- runs of pipe: legs, bends, and branches with welding tees;
- sections;
- in-line components: valves, flanges, rigids, reducers, expansion joints and couplings;
- supports by type or by single degree of freedom, with gaps, stiffness, friction, skewed directions and CNODEs;
- node-to-node ties;
- SIFs;
- temperatures, or a ΔT from the ambient;
- pressures;
- loads: forces and moments, imposed displacements, uniform loads (per length or in g's) and wind.

`-Preview` also shows the model in the viewer through the CAESAR arm ([[vault/arms/caesar-to-viewer|caesar-to-viewer]]), support symbols included, before CAESAR sees it.

The same runner takes an **existing CAESAR job** too: `-In job.cii` imports it losslessly (everything the model does not edit is carried as written, and an unedited job is written back byte for byte), and `-In x.pipe.json` reads a model saved with `-Json`. The model, the carry rules and the JSON: [[vault/format/pipe-model|pipe-model]]. **An Excel workbook** is the model's editable master copy: `-Xlsx` writes one, `-In x.xlsx` reads it back, `-Watch` rebuilds the preview on every save ([[vault/arms/pipe-workbook|pipe-workbook]]).

**Status: verified on Linux, still `draft`.**
- **Checked:**
  - a strict C# 5 compile;
  - a round trip of three configs: TOML → `.cii` → the CAESAR arm's own reader and geometry, compared field by field and node by node;
  - 32 failure cases;
  - the runner under pwsh;
  - the viewer.
- **Still to do:**
  - the PS 5.1 compile on the production machine;
  - the first import into CAESAR II.

Same model, other solver: a SAP2000 `.s2k` from the same `PipeModel`, as a check model of CAESAR's, comes after the workbook and the loads (see *Next*).

## Run

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 `
  -In "<project dir>\line.toml" -Out "<project dir>\line" -Cii -Preview

powershell.exe ... -File scripts\caesar\Build-Pipe.ps1 -In "<project dir>\job.cii" -Compare "<project dir>\job_from_caesar.cii"
```

- **`-In`** (alias `-Config`), one of:
  - a `.xlsx` workbook ([[vault/arms/pipe-workbook|pipe-workbook]]); the report lists its edits;
  - a `.toml` config. Every key is shown, with its unit, in `scripts/caesar/examples/pipe-example.toml`;
  - a `.cii`, an existing CAESAR job. The report says whether writing it back gives the source identically;
  - a `.pipe.json`, a model saved with `-Json`.
- **Outputs**, in any combination:
  - **`-Cii`**: `<base>.cii`, to import in CAESAR II through File → Import → CAESAR II Neutral File, and for a `.toml` `<base>.nodes.csv`, with every node's run, position along it, x y z, and what is there. The default for a `.toml` when no output is chosen. Never written over the input.
  - **`-Xlsx`**: `<base>.xlsx`, the model as a workbook to edit.
  - **`-Json`**: `<base>.pipe.json`, the whole model, exactly.
  - **`-Preview`**: `<base>.bin` + `<base>.features.json`, built in memory from exactly the `.cii` that `-Cii` writes (what `Run-Caesar.ps1` would make of it).
  - With none of them, a `.cii` or `.pipe.json` gives a report only.
- **`-Compare <file>`**: the model against another `.cii`, `.toml` or `.pipe.json`, by meaning: elements, node positions, restraints, SIFs, carried blocks, settings. For example, against CAESAR's own re-export of the job.
- **`-Seed <job.cii>`**: a `.toml` model (or `-NewWorkbook`) takes that job's VERSION lines, UNITS (English only), execution options and first allowables block.
- **`-Watch`**, **`-NewWorkbook`**, **`-Force`** and the `-Cii` guard: [[vault/arms/pipe-workbook|pipe-workbook]].
- **`-Out <base>`**:
  - Default: beside the input, same name.
  - An existing folder, or a path ending in `\` or `/`, means `<folder>\<name>`.
  - **Output inside the Pluto repo is refused.**
- **`-Set "span=25;dT=250"`** overrides a `.toml`'s `[parameters]` values, as numbers or expressions.
- **`-ModelId`**: default `pipe/<name>`.
- **Output:**
  - Progress lines start with `[step]`.
  - The last line is `RESULT {json}`: paths, counts and warnings.
  - An input that does not build or import, or a file that cannot be written, prints `[error  ] <message>` and `RESULT {"error": …}`, and exits 1.
  - Every message names the config item it comes from, e.g. `run "header": support at 39.5 ft is inside the bend at corner 1 (its arc runs 39 ft .. 41 ft; …)`.
  - **An unknown key is an error**, so a typo never passes silently.

## The config

> [!info]- Units and expressions
> - **Geometry** (leg lengths, positions along a run, start points): `model.length_unit`, `"ft"` (default) or `"in"`.
> - **Inches:** diameters, walls, insulation, corrosion, bend radii, translational gaps.
> - **Degrees:** rotational gaps and coupling deflections.
> - **Other units:**
>   - temperatures °F;
>   - pressures psi;
>   - densities lb/ft³;
>   - weights lb;
>   - stiffness lb/in (translation) and in·lb/deg (rotation);
>   - forces lb, moments in·lb, imposed displacements in and deg;
>   - uniform loads lb/in, or g's.
> - **Expressions:** any number may be an expression string over `[parameters]`, e.g. `length = "3*span + 2"`.
>   - Operators: `+ - * / ^` and parentheses.
>   - Functions: `pi`, `sqrt abs min max round floor ceil hypot`, `sin cos tan asin acos atan atan2` (in degrees).
>   - Parameters may refer to each other in any order. A cycle is reported.
> - **Positions** along a run may also use `L`, the run's length: `at = "L"`, `at = "L - 2"`.
> - CAESAR gets its English set: inches, lb, °F, psi, lb/in³.

> [!info]- [model], [parameters], [defaults], [sections]
> **`[model]`:**
> - `material` (required): the CAESAR material number, e.g. 102 = A53 Grade B. E, density and the allowables come from CAESAR's material database (the fixture's job does the same).
> - `ambient`, default 70 °F: the installation temperature.
> - `title`.
> - `length_unit`.
> - `node_step`, default 10.
> - `y_up`, default true; false = Z up.
> - `code`: only `"B31.1"` is built in. For another code, `allowables_from = "<a .cii exported from CAESAR with that code>"` copies its allowables block.
>
> **`[parameters]`:** `name = number | "expression"`.
>
> **`[defaults]`:** what every run gets unless it says otherwise:
> - `section`;
> - `temperature` or `delta_t`;
> - `pressure`;
> - `bend` (`"LR"` = 1.5 × nominal, `"SR"` = 1 × nominal, a radius in inches, or `"none"` for a sharp corner);
> - `bend_nodes` (`"mid"`: the TO − 1 node at the bend's mid point, CAESAR's own default; `"near-mid"` adds TO − 2 at the near point; `"none"`);
> - `max_length`: the longest straight between nodes.
>
> **`[sections.NAME]`** (or `[[sections]]` with `name`):
> - `nps` + `schedule` (NPS ½–24; STD, XS, 40, 80, 160; ASME B36.10M), or `od` + `wall` in inches;
> - `insulation` (in) + `insulation_density`;
> - `fluid_density` or `fluid_sg` (× 62.4 lb/ft³);
> - `corrosion`;
> - `material`, to override the model's.

> [!info]- [[runs]]: legs, bends, branches, temperatures
> - **Start:** `start = [x, y, z]`, the first run's default being the origin. Or `from = { run = "header", at = 12 }`: a branch, starting at that position on an earlier run.
>   - A branch gets a **welding tee** at the header node (SIF type 3, as CAESAR writes one).
>   - Opt out with `tee = "none"`. That is the default when the branch starts at the other run's end, which makes it a continuation.
> - **`legs`:** each leg is `["+X", 20]` (`±X ±Y ±Z`, or `[x, y, z]` for a skewed direction), or one of these tables:
>   - `{ dir = …, length = …, bend = … }`;
>   - `{ to = [x, y, z] }`;
>   - `{ by = [dx, dy, dz] }`.
>
>   A leg's `bend` is the bend at the END of that leg. It overrides the run's and `[defaults]`.
> - **Per run:**
>   - `section`;
>   - `temperature = T` or `[T1, T2, …]`, up to 9;
>   - **or** `delta_t = ΔT` or a list, giving T = ambient + ΔT. CAESAR computes the thermal strain from T − ambient.
>   - `pressure = P` or `[P1, …]`;
>   - `fluid_density` / `fluid_sg`, overriding the section's;
>   - `line`: the line number, written on the run's first element; CAESAR carries it forward;
>   - `bend`, `bend_nodes`, `max_length`;
>   - `first_node`.
> - A temperature of exactly 0 is CAESAR's "not used". A value under 1 in magnitude is read by CAESAR as an expansion coefficient. Both are warned.

> [!info]- [[components]]: valves, flanges, rigids, reducers, expansion joints, couplings
> All take `run`, `at` (where the component starts) and `length` (its own length), plus `name`.
> - **`valve` `flange` `flange_pair` `flanged_valve` `rigid`:** `weight` (lb). These become a CAESAR rigid of type Valve / Flange / Flange Pair / Flange Valve / Unspecified.
> - **`reducer`:** `to_section`, the section after it. Every element downstream takes it, and so do the bend radii (LR / SR) of the corners after it.
> - **`expansion_joint`:**
>   - `axial_stiffness` (required, lb/in);
>   - `transverse_stiffness` (lb/in) **or** `bending_stiffness` (in·lb/deg). For a joint with length, CAESAR derives one from the other, so give one;
>   - `torsion_stiffness`, rigid unless given;
>   - `effective_id` (in) for the pressure thrust. Without it there is no thrust, and a warning.
> - **`coupling`** (a mechanical coupling): nodes N and N + 1 at one point, tied by **6 CNODE restraints in the pipe's own axes**:
>   - axial: `axial_gap` (in, free each way), then `axial_stiffness`, rigid unless given;
>   - two shears: rigid;
>   - torsion: rigid;
>   - two bendings: `deflection` (deg, free), then `rotational_stiffness`, rigid unless given;
>   - optional `sif`.
>
>   The fixture's CAESAR job holds the same pattern: a node tied to node + 1 in X Y Z RX RY RZ, with a rotational gap and user SIFs. On a skewed pipe the restraints carry cosines.

> [!info]- [[supports]], [[ties]], [[sifs]]
> **`[[supports]]`:**
> - **Where:** `node = n`, or `run` + `at = x | [x, …]`, or `run` + `every = d` (`from` = d and `to` = L unless given).
> - **What:**
>   - `type = "+Y"` or a list (`ANC X Y Z RX RY RZ GUI LIM +X +Y +Z -X -Y -Z +RX +RY +RZ -RX -RY -RZ +LIM -LIM XSNB YSNB ZSNB`, CAESAR's codes 1–26);
>   - or `dofs = ["X", "RY", …]`: single degrees of freedom, e.g. an equipment nozzle as six DOFs with rotational stiffness.
> - **Options:** `gap`, `stiffness` (0 = rigid) and `mu`, each a number for every type, or a table per type: `gap = { GUI = 0.0625 }`, `stiffness = { RX = 2.5e6 }`. A key that is not one of the support's types is an error.
> - `direction = [x, y, z]` with one translational or rotational type: along a global axis it gives that axis' type; any other direction gives a skewed restraint, X or RX with the direction's cosines.
> - `cnode = n` and `tag`.
>
> **`[[ties]]`:** `node` and `cnode`, each a number or `{ run, at }`, plus the same `type` / `dofs` / `gap` / `stiffness` / `mu` / `direction` / `tag`. For example, a strut between two lines: `type = "Z"`, `gap = 0.125`.
>
> **`[[sifs]]`:** `type = "welding"` (a welding tee), or `sif_in` / `sif_out` alone (user SIFs; `sif_out` defaults to `sif_in`). One SIF / tee entry per node: a branch's automatic tee counts. Give the branch `tee = "none"` to write your own.

> [!info]- [[forces]], [[displacements]], uniform loads and wind
> **`[[forces]]`:** forces and moments at nodes.
> - **Where:** `node = n`, or `run` + `at` (or `every`), like a support. A position makes a node there.
> - `vector` (1–9, default 1): CAESAR's load vector F1…F9.
> - `fx fy fz` (lb), `mx my mz` (in·lb); at least one.
> - Entries at one node merge into one CAESAR entry; the same node and vector twice is an error.
>
> **`[[displacements]]`:** imposed displacements at nodes (anchor movements, equipment growth).
> - The same `node` / `run` + `at` and `vector` (D1…D9).
> - `dx dy dz` (in), `rx ry rz` (deg). **A DOF not given stays free; a 0 holds it** in that vector.
>
> **Uniform loads and wind**, on a run or in `[defaults]` (a run's own value wins):
> - `uniform = [x, y, z]` in lb/in is U1; a list of them gives U1, U2 …; `0` means none.
> - `g_load = [x, y, z]` in g's (seismic, for example) takes the next vector after `uniform`'s. At most 9 together.
> - `wind_shape = 0.7`: the wind shape factor; `0` means no wind on that run.
> - Every element of the run gets its own uniform and wind block, as CAESAR writes them. The neutral file does not carry them forward, so an element without a block has none.
>
> Load cases are not in the neutral file: CAESAR builds them and recommends a set on import. The layouts and what is confirmed: [[vault/format/caesar-neutral-loads|caesar-neutral-loads]].

## Geometry rules (CAESAR's own)

- **Bends follow CAESAR's convention.** A corner between two legs gets a bend. The element carrying the bend runs to the corner (the tangent intersection point), and its TO node is the bend's **far point**. The next element is measured from the corner too.
- **Positions along a run are measured along the leg lines through the corners.** So a position *at* a corner is the bend's far point. The arc occupies `corner ± T`, with T = R tan(θ/2), and **nothing else may go there**: the error message gives the arc's extent.
- **A component placed at a corner** starts at the far point and keeps its own length. For example, a flange welded to an elbow: `at` = the corner, `length` = the flange's.
- **A component cannot end at a bend's corner.** End it at the near point, `corner − T`.
- **Room for the bends:** each leg must hold its bends' tangents: T at the start, T₁ + T₂ between two corners. Back-to-back bends, with no straight between them, are allowed.
- **`every` skips positions that fall in a bend's arc or inside a component**, with a warning that lists them. An explicit `at` there is an error.
- **Couplings** sit on a straight: not at a corner, not inside a component.
- **Not allowed:** a branch from a bend's far point.
- **Nodes** are made only where something needs one:
  - run ends and corners;
  - supports;
  - component ends;
  - couplings;
  - branch points;
  - SIFs;
  - tie ends;
  - `max_length` divisions of the straights, never inside a component.
- **Node numbers:**
  - The first run counts from `node_step`, in steps of `node_step`.
  - Every other run starts at the next free hundred (or `first_node`).
  - A bend's own nodes are TO − 1 (mid) and TO − 2 (near).
  - A coupling's second node is N + 1.
  - The node table (`.nodes.csv`) lists them all.
- **COORDS:** every run that is not a branch gets a COORDS entry for its first node. The builder checks that every new FROM node starts where the previous element ended, which is where CAESAR would put it.

## What the neutral file holds

`CaesarNeutralWriter` writes the 15.01 layout. Its formatting re-emits the generic fixture byte for byte: 2,482 numeric and 790 text lines.

> [!info]- Per element and per block
> - **Element rows:** OD, wall, insulation, corrosion, T1–T9, P1–P9, insulation and fluid density.
>   - E, Poisson and pipe density are 0: CAESAR takes them from the material database, by the MISCEL_1 material number.
> - **Bends:** the radius, the mid node (angle code −2.0202, CAESAR's "M") and the near node (angle 0), with the fitting thickness = the wall, as CAESAR writes it.
> - **Restraints:**
>   - node, type code, stiffness (1e12 = rigid), gap, friction, CNODE, cosines, tag;
>   - **cosines as CAESAR writes them:** every type but ANC carries its axis (X 1 0 0, Y and +Y 0 1 0, …), and GUI / LIM a dummy 1 0 0, CAESAR taking the pipe axis;
>   - up to 6 per element block, placed on an element that has the node.
> - **SIF / tees:** type 3 (welding tee) or user SIFs in / out. Rigids: weight and type. Expansion joints: axial, transverse, bending, torsion, effective ID. Reducers: OD2 and wall2; α, R1 and R2 are left to CAESAR.
> - **Allowables, options, units, COORDS:**
>   - **ALLOWBLS:** the fixture's B31.1 block, values from the database, or a seed's block;
>   - **MISCEL_1:** the material numbers and the fixture's execution options, with the ambient temperature in line 2;
>   - **UNITS:** English;
>   - **COORDS:** in ft.

> [!info]- Loads
> - **FORCMNT / DISPLMNT:** two entries per block, each the node and then one line per load vector.
> - **UNIFORM:** 9 vectors of UX UY UZ and a g flag.
> - **WIND:** type and shape factor.
>
> Layouts: [[vault/format/caesar-neutral-loads|caesar-neutral-loads]].

> [!info]- Confirmed against a CAESAR-written file, and not yet
> - **Confirmed by the 15.01 fixture** (`scripts/caesar/testdata`, read back by the CAESAR arm):
>   - element rows;
>   - bends with mid nodes;
>   - rigid types; expansion joint fields; reducers;
>   - restraint types ANC X Y Z RX RY RZ GUI LIM +Y, with stiffness, gaps (rotational in degrees), friction, CNODEs and cosines;
>   - the coupling pattern;
>   - welding tees and user SIFs;
>   - COORDS;
>   - the ambient field.
> - **Confirmed by a CAESAR 15.01 export (2026-10-22):** FORCMNT, UNIFORM and WIND (wind type and shape factor), one uniform / wind block per loaded element.
> - **From CAESAR's documentation of the neutral file; no CAESAR-written file has shown them yet.** Each is listed as a `[notice ]` line when written; check them after the first import:
>   - restraint codes beyond 1–9, 14, 15 (`+X` 13, `-X -Y -Z` 16–18, one-way rotations, `±LIM`, snubbers);
>   - wave data in WIND;
>   - DISPLMNT in 15.01's number format (its layout is confirmed by 11.00 jobs).
> - **Written, but neither documented nor shown:**
>   - skewed restraints (X / RX with the direction's cosines), also a `[notice ]`;
>   - a bend's near node at angle 0 (`bend_nodes = "near-mid"`).
> - **Not written:**
>   - hangers (CAESAR's hanger design);
>   - nozzle flexibility (WRC 297);
>   - load cases. CAESAR recommends cases on import.
> - **Also not yet:** a hydrotest pressure (its field is unconfirmed), flanged-bend types and mitres, and tee types other than welding.

## Verification (2026-10-07, Linux)

- **Strict compile** of the whole `scripts/lib` + `scripts/arms` batch: mono, C# 5, against the .NET Framework 4.8 reference assemblies. 0 errors, no new warnings.
- **Round trip:** TOML → `PipeBuilder` → `.cii` → `CaesarNeutralReader` + `CaesarGeometry`, compared against the builder's own model:
  - every node position (worst 1.7e-4 in, from the 7 significant digits of COORDS in feet);
  - every element field;
  - bends, rigids, expansion joints, reducers;
  - all restraints as a multiset;
  - SIFs, COORDS, IZUP, the ambient;
  - no reader or geometry warnings;
  - every bend drawn as a bend.
- **Configs:**
  - the example: 3 runs, 41 nodes, 34 restraints, a coupling, a tie, a branch tee;
  - a skewed one: a 53° bend between skewed legs, back-to-back bends, a reducer before a bend, a flange at a corner, a coupling on a skewed leg, a skewed tie;
  - a Z-up config in inches with ΔT lists.
- **32 failure cases** each give the expected message: bends without room, features inside an arc or a component, overlaps, typos, cycles, unknown sections, 7 restraints on one element end, and so on.
- **`Build-Pipe.ps1` under pwsh** with the compiled batch: build, write, `-Set` and `-Preview`.
- **Viewer:** the preview opens with all 21 support symbols and no errors.

## Verification of the loads (2026-10-22, Linux)

- **A test config with every load kind**, on a header, a branch and a tail:
  - forces in two vectors;
  - a displacement with every DOF imposed in D1 and one DOF in D2;
  - uniform loads per length, a g load on the branch, wind on the header and tail and none on the branch.
- **What is checked:**
  - It builds, writes, and reads back with no reader warnings, and the CONTROL counts are right.
  - Every block has the documented layout: the node alone, then one line per vector; 9999.99 for free DOFs and for the unused slot; 36 uniform values with the g flags; one wind line.
  - Every element has its own uniform block; wind blocks only on the header and tail elements.
  - Read back, it is byte-identical. Compare against the built model finds no differences.
- **Edits on the imported file:**
  - Changing a force, a uniform value, the wind, a freed DOF or a g flag changes one line each.
  - A new force at a node without one adds one 20-line block. A second entry at a loaded node fills its block's free slot.
  - Removing a node's forces removes its block, and compare reports exactly that.
- **An 11.00 job's displacements:** read, edited and extended, one line each.
- **Errors:** 8 config mistakes give their message.
- **Unchanged:** every Stage A check (outputs of configs without loads, the 22 samples, JSON, compare, the 32 failure cases), and `Build-Pipe.ps1` under pwsh with the example (now with loads).

## Next

The plan, in stages ([[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]]):
- **A, done (2026-10-22):** the lossless `.cii` import, the overlay-and-carry writer, the model JSON, compare, and `-In` / `-Json` / `-Compare` / `-Seed` ([[vault/format/pipe-model|pipe-model]]).
- **B, done (2026-10-22): the Excel workbook** as the model's master, a `.cii`, `.toml` or template into a workbook and back with nothing lost; `-Watch`; Reload / Auto-reload in the viewer ([[vault/arms/pipe-workbook|pipe-workbook]]).
- **C, done (2026-10-22): loads**, from CAESAR's documentation of the neutral file: forces and moments, imposed displacements, uniform and g-loads, wind, in the model, the importer, the writer, the TOML (`[[forces]]`, `[[displacements]]`, `uniform` / `g_load` / `wind_shape`), the model JSON and compare ([[vault/format/caesar-neutral-loads|caesar-neutral-loads]]).
- **D: the viewer as the confirmation step:** loads drawn, element properties on hover, node numbers, temperature / pressure / material groups, and a list of build checks.
- **E: a SAP2000 `.s2k`** as a check on CAESAR: the same pipe and loads, with equivalent fixities, supports, restraints and releases (gaps and one-ways as gap links; friction reported, as SAP cannot model it as CAESAR does). It runs through the one SAP controller, `Run-Sap.ps1 -Open x.s2k -Run` ([[vault/arms/sap-controller|sap-controller]]); no second driver.
- **On the production machine:** the first CAESAR import of the example, then a real line.
