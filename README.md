# Pluto

A FEMAP-style hub for finite-element data: solver **import/export arms** converge on one
common model (a v4 binary for results + geometry, a JSON **features sidecar** for
everything a user defines), and one generic **viewer** consumes it.

```text
  Solver A  ──import──┐                       ┌──export──▶  Solver A
  Solver B  ──import──┤                       ├──export──▶  Solver B
  Solver C  ──import──┼──▶  common model  ──▶ ┤
  ...                 │      (Pluto core)     └──export──▶  ...
                      └──▶  Viewer arm  (generic point of convergence)
```

**Documentation lives in the Obsidian vault — start at `vault/Pluto Home.md`, whose up-link opens the full tool note in the Notes vault.**
The current state and next task are in the Notes vault handoff (`Tools/Pluto Handoffs/`).

## Repo layout

```text
vault/             Obsidian vault (vault root = repo root) — ALL documentation
scripts/           production C# 5 / PowerShell 5.1 toolchain: STAAD post-processing lib,
  lib/               v4 writer, sidecar writer — one Add-Type batch via lib/Config.ps1
  arms/              pipe / steel / combined CSV / SAP2000 -> .bin + sidecar
viewer/            the viewer (open viewer/index.html; no build, no server)
pythonTools/       development-side Python (sap/: SAP2000 tank generator -> .s2k)
archive/           superseded code (old viewer, v3 writer) -- reference only
```

## Run the viewer

Open `viewer/index.html`. A synthetic demo loads; use the file picker to open a real
`.bin` (drop its `.features.json` alongside for groups). `viewer/inspector.html` dumps a
file field by field.

**Project data is not in this repo.** Real tank configs and SAP models live in a local project folder outside the repo and are passed to the scripts by path; `[output] dir` in a config, or the default `../models` beside it, decides where a model lands. Project findings and drawings are notes in the Notes vault, not here.
