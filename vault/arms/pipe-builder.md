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
- pressures.

`-Preview` also shows the model in the viewer through the CAESAR arm ([[vault/arms/caesar-to-viewer|caesar-to-viewer]]), support symbols included, before CAESAR sees it.

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

Same model, other solver: a SAP2000 `.s2k` writer from the same `PipeModel` is the next stage (see *Next*).

## Run

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\caesar\Build-Pipe.ps1 `
  -Config "<project dir>\line.toml" -Out "<project dir>\line" -Preview
```

- **`-Config`** is the TOML. Every key is shown, with its unit, in `scripts/caesar/examples/pipe-example.toml`.
- **`-Out <base>`** writes:
  - `<base>.cii`, to import in CAESAR II through File → Import → CAESAR II Neutral File;
  - `<base>.nodes.csv`, with every node's run, position along it, x y z, and what is there.
  - Default: beside the config, same name.
  - An existing folder, or a path ending in `\` or `/`, means `<folder>\<name>`.
  - **Output inside the Pluto repo is refused.**
- **`-Set "span=25;dT=250"`** overrides `[parameters]` values, as numbers or expressions.
- **`-Preview`** also writes `<base>.bin` + `<base>.features.json`. These are exactly what `Run-Caesar.ps1` makes of the written `.cii`.
- **`-ModelId`**: default `pipe/<name>`.
- **Output:**
  - Progress lines start with `[step]`.
  - The last line is `RESULT {json}`: paths, counts and warnings.
  - A config that does not build prints `[error  ] <message>` and `RESULT {"error": …}`, and exits 1.
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
>   - stiffness lb/in (translation) and in·lb/deg (rotation).
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
>   - `type = "+Y"` or a list (`ANC X Y Z RX RY RZ GUI LIM +X +Y +Z -X -Y -Z`);
>   - or `dofs = ["X", "RY", …]`: single degrees of freedom, e.g. an equipment nozzle as six DOFs with rotational stiffness.
> - **Options:** `gap`, `stiffness` (0 = rigid) and `mu`, each a number for every type, or a table per type: `gap = { GUI = 0.0625 }`, `stiffness = { RX = 2.5e6 }`. A key that is not one of the support's types is an error.
> - `direction = [x, y, z]` with one translational or rotational type: along a global axis it gives that axis' type; any other direction gives a skewed restraint, X or RX with the direction's cosines.
> - `cnode = n` and `tag`.
>
> **`[[ties]]`:** `node` and `cnode`, each a number or `{ run, at }`, plus the same `type` / `dofs` / `gap` / `stiffness` / `mu` / `direction` / `tag`. For example, a strut between two lines: `type = "Z"`, `gap = 0.125`.
>
> **`[[sifs]]`:** `type = "welding"` (a welding tee), or `sif_in` / `sif_out` alone (user SIFs; `sif_out` defaults to `sif_in`). One SIF / tee entry per node: a branch's automatic tee counts. Give the branch `tee = "none"` to write your own.

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
> - **Written, but no CAESAR file has shown them yet.** Check these after the first import:
>   - the type codes of `+X` `-X` `-Y` `-Z` (13, 16, 17, 18, by CAESAR's type list). They are written with a warning.
>   - skewed restraints (X / RX with the direction's cosines). Written with a warning.
>   - a bend's near node at angle 0 (`bend_nodes = "near-mid"`).
> - **Not written yet.** A small job exported from CAESAR with one of each confirms their 15.01 layout first. Until then a config table for them is refused with that message.
>   - loads: forces / moments, uniform loads, wind;
>   - imposed displacements;
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

## Next

- **SAP2000 `.s2k` writer** from the same `PipeModel`:
  - frames with pipe sections;
  - bends as chord frames;
  - restraints as joint restraints / springs / links (gaps as gap links);
  - temperature loads from ΔT.

  It is a structural model only: no code stresses, SIFs or pressure effects. It is run through the one SAP controller, `Run-Sap.ps1 -Open x.s2k -Run` ([[vault/arms/sap-controller|sap-controller]]); no second driver.
- **Loads, displacements, hangers and nozzles** once a seed export confirms their layout.
- The first CAESAR import of the example, then a real line, on the production machine.
