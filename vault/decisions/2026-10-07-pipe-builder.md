---
title: One parametric pipe model, written for CAESAR II first
status: current
created: 2026-10-07
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

Pipe-stress models are built from a **TOML config** by C# in the batch:
- `scripts/arms/PipeBuilder.cs`, with `TomlReader` in `scripts/lib/readers` and `PipeExpr` for the expressions.
- It produces a solver-neutral **`PipeModel` in CAESAR II's terms** (`scripts/arms/PipeModel.cs`).
- Writers turn the model into solver files.

The choices:
- **CAESAR II neutral file first.** `CaesarNeutralWriter` writes the `.cii`. A SAP2000 `.s2k` writer from the same model comes later, run through the one SAP controller.
  - **Since (2026-11-12):** `Build-Frame.ps1 -In <job>` seeds that `.s2k`, the SAP check model of the pipe ([[vault/format/pipe-to-frame|pipe-to-frame]], [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]]).
- **Temperatures are a ΔT from the ambient** (`delta_t`, or an absolute `temperature`). The ambient goes in the neutral file, and CAESAR takes the thermal strain from T − ambient.
- **Couplings come in both kinds** the user asked for:
  - a mechanical coupling is a component: nodes N and N + 1 tied by 6 CNODE restraints in the pipe's axes, with an axial gap, an angular deflection allowance and stiffnesses;
  - a tie is a free node-to-node CNODE restraint set in chosen DOFs with gaps.
- **Positions follow CAESAR's own conventions.** Positions along a run are measured along the leg lines through the corners, so a corner's node is the bend's far point. A neutral file is never re-shaped to fit a different convention.
- **No layout is guessed.** A block the 15.01 fixture has not shown (loads, imposed displacements, hangers, nozzles) is refused with a message until a CAESAR export confirms it. Restraint codes and cosines not yet seen are written with a warning.
  - **Since (2026-10-26):** loads, imposed displacements, hangers and nozzles are written in layouts CAESAR 15.01 has confirmed; only a restraint code past 26 gets a `[notice ]` line ([[vault/format/caesar-neutral-loads|caesar-neutral-loads]], [[vault/decisions/2026-10-24-hangers-nozzles-in-model|2026-10-24-hangers-nozzles-in-model]]).

Runner: `scripts/caesar/Build-Pipe.ps1`. Usage: [[vault/arms/pipe-builder|pipe-builder]].

## Why

- **TOML** is what the tank builder uses ([[vault/arms/sap-tank-builder|sap-tank-builder]]), so one config dialect serves both. `[parameters]` with expressions and `-Set` overrides make the model parametric without a scripting language.
- **C#, not Python:** the builder must run on the production machine (PS 5.1, no Python). `scripts/` may not need Python.
- **CAESAR's terms in the model** because CAESAR is the stress program of record: code stresses, SIFs and pressure effects exist only there. SAP gets a structural approximation from the same model.
- **The fixture fixes the format.** The writer re-emits the generic 15.01 fixture byte for byte. Every builder feature is read back through the CAESAR arm's own reader and geometry.
- **Considered and not chosen:**
  - a Python builder beside the tank generator: it is not available on the production machine;
  - writing CAESAR's XML instead of the neutral file: the XML has no verified writer here, while the neutral file has a fixture and a reader.

## Status

- **Stage 1 (2026-10-07):** the builder, the writer, the runner with `-Preview`, and the example config are on branch `pipestress`. They are verified on Linux.
- **Still to do:**
  - the PS 5.1 compile;
  - the first import into CAESAR II (done 2026-10-24: three probe jobs imported into CAESAR 15.01, [[vault/format/caesar-neutral-loads|caesar-neutral-loads]]);
  - a seed export for the load blocks (done 2026-10-22: a CAESAR 15.01 export with FORCMNT, UNIFORM and WIND, [[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]]);
  - the `.s2k` writer (done 2026-11-12: the SAP check model, [[vault/format/pipe-to-frame|pipe-to-frame]]).
