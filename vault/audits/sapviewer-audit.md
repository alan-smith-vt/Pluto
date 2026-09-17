---
title: "Audit — SapViewer fold-in"
status: current
created: 2026-09-03
---

*↑ [[vault/Pluto Home|Home]] › [[vault/audits/Audits map|Audits map]]*

Quick read-only audit of `../SapViewer` (2026-09-03) ahead of folding it into Pluto.

**Status 2026-09-03 (later):** folded in as C#, not Python -- `scripts/arms/SapToPluto.cs`
(usage: [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]]); Python archived to
`pythonTools/sap/`. The UI redesign (`sample/`, `docs/ui/`) was **not** carried over by
user decision (existing Pluto UI preferred). Items 3-5 below are therefore closed.
Companion: the current handoff (Notes vault, `Tools/Pluto Handoffs`), [[vault/format/v3-schema|v3-schema]].

## What it is

SAP2000 arm for cylindrical steel tanks (ring-wall base, anchor chairs, hydrostatic
pressure, settlement). Pipeline: `configs/*.toml` → `sap/build_tank.py` → `.s2k` →
SAP2000 → results `.s2k` → `sap/s2k_to_bin.py` → **FEAV v3 `.bin`** → `sample/` viewer.
Units kip/ft/°F. Python 3 (argparse, struct, TOML); no C# beyond a vendored
`sample/RawViewerWriter.cs`. Branch `claude/ready-to-start-l75hyv`; last commit
"Tabbed panel redesign"; one uncommitted change (unit comments in `configs/tank_hoop.toml`).

| path | verdict |
|---|---|
| `sap/build_tank.py`, `sap/s2k_to_bin.py`, `configs/` | **genuinely new** — the SAP arm. Nothing in Pluto reads/writes `.s2k`. |
| `sample/` | a **fork of the pre-v4 Pluto viewer** (same file names). `pointQuery.js` identical, `sectionCut.js`/`inspector.js` ≤4 lines apart, but `viewer.js` (~470 lines), `attributeUpdaters.js`, `modelSet.js`, `shaders.js`, `viewCube.js`, `geometryBuilder.js`, `sampleModel.js` diverge heavily — the tabbed-panel UI redesign (`docs/ui/`) lives here and not in Pluto. `binaryReader.js` has no Pluto counterpart (Pluto moved to `scripts/format/`). |
| `sample/RawViewerWriter.cs` | v3 writer, ~130 non-whitespace lines different from the sanitized production copy; superseded by Pluto's v4 writer. Drop. |
| `three-navigation/` | camera/ViewCube reference rig; Pluto already has `viewCube.js`. Reference only. |
| `tank_hoop.bin` (root, 484 kB) + two tracked `three.min.js` (592 kB each) | tracked binaries; `models/` (13 MB of SAP working files) is correctly gitignored. |

## Sanitization / quality

- No secrets, no hardcoded machine paths, no `// sanitized` husks, no UNC shares.
  Model is a generic tank — nothing proprietary spotted.
- `sap/__pycache__/` is tracked (add to `.gitignore`).
- C# in `sample/RawViewerWriter.cs` is C# 5-clean (lambdas only, no `$""`/`?.`/`nameof`).
  Irrelevant once dropped.
- `sample/lib/OrbitControls.js` differs from Pluto's copy — check version before merging.

## Recommended fold-in shape

1. `sap/build_tank.py`, `sap/s2k_to_bin.py`, `configs/` → `scripts/exporters/sap/` (or
   `arms/sap/`), keeping the Python as-is; new arm note `vault/arms/sap-tank-to-viewer.md`
   modelled on [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]].
2. `s2k_to_bin.py` writes **v3**; Pluto loads v3 through the shim, so it works today.
   Later: teach it v4 (`LABL` block, sidecar groups for wall/base/chairs) so the SAP arm
   gets predicates and Color-by-groups for free.
3. Do **not** merge `sample/` wholesale. Cherry-pick the UI redesign (tabbed panel,
   type tokens, docked section cuts — `docs/ui/README.md` is the spec) onto Pluto's
   `viewer/` as its own phase; diff `viewer.js`/`attributeUpdaters.js` for it.
4. Drop `sample/RawViewerWriter.cs`, `three-navigation/`, both `three.min.js`, root
   `tank_hoop.bin` (keep `configs/` and regenerate).
5. Carry the `docs/ui/` note + SVG mockups into `vault/viewer/ui-redesign/`.
