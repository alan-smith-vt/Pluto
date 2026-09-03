---
title: SAP2000 .s2k → viewer (usage)
status: current
created: 2026-09-03
---

# SAP2000 `.s2k` → viewer — usage

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Shell models and results from SAP2000's text export to a Pluto v4 file + features
sidecar. One arm: `scripts/arms/SapToPluto.cs`, built on the lib's existing
`Sap2kParser` / `Sap2kReader` (`scripts/lib/readers/SapReader.cs`, which the STAAD
pathway also uses) and the v4 writer. Replaces the Python `s2k_to_bin.py` from the
SapViewer repo (archived: `pythonTools/sap/`), which wrote v3.

Verified 2026-09-03 on the SapViewer tank model (`tank_hoop`, 756 joints / 720 quads,
cases DEAD + HYDRO): PS 5.1 compile via `Config.ps1`, export, and a headless readback with
the viewer's own `PlutoFormat.load` (`node viewer/tests/readback_sap.js <outBase>`) —
force values match the `.s2k` rows to float precision, joint-local displacements come
out radial-outward in global, SAP Z-up axes preserved.

## 1. Export from SAP2000

- **Model**: File → Export → SAP2000 `.s2k` Text File. Tick the model tables (all is
  fine). `JOINT COORDINATES`, `CONNECTIVITY - AREA`, `PROGRAM CONTROL` are required;
  `AREA SECTION ASSIGNMENTS` / `AREA SECTION PROPERTIES`, `JOINT RESTRAINT ASSIGNMENTS`,
  `JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL`, `GROUPS 2 - ASSIGNMENTS` feed the sidecar.
- **Results** (optional, after the run): same export ticking **Element Forces – Area
  Shells** (output *at joints*, not element centres only) and **Joint Displacements**.
  Can be the same file as the model.
- Joint and Area **labels must be integers** (SAP default). Otherwise: Edit → Change
  Labels, then export. The arm throws naming the first offender.
- Units: whatever `CurrUnits` says (`Kip, ft, F` for the tank); values are written raw
  and the unit strings go into the component list and the sidecar.

## 2. Run (PowerShell 5.1, fresh window)

```powershell
. <repo>\scripts\lib\Config.ps1   # one Add-Type batch: scripts/lib + scripts/arms
$r = [SapToPluto]::Export('C:\Temp\tank.$2k', 'C:\Temp\results.s2k', 'C:\Temp\tank', 'sap/tank')
$r.Summary()
# geometry-only (no results yet): pass $null for the results file
$g = [SapToPluto]::Export('C:\Temp\tank.$2k', $null, 'C:\Temp\tank_geom', 'sap/tank')
# tanks / silos: add Translation R / T resolved about the plan centroid of all joints
$r = [SapToPluto]::Export($model, $results, $outBase, $modelId, $true)
```

Writes `<outBase>.bin` + `<outBase>.features.json`. Drop both on the viewer together, tick
**Z up** (SAP axes are kept as-is — no STAAD rotation on this pathway; the STAAD pathway's
`Sap2kReader.ExtractGeometry(path)` still rotates, `ExtractGeometry(path, false)` does not).

## 3. What lands where

| SAP table | Pluto |
|---|---|
| `JOINT COORDINATES` (+ `COORDINATE SYSTEMS`) | nodes, f64, global SAP axes; joint label → node `LABL` |
| `CONNECTIVITY - AREA` | shell domain, tri/quad; area label → shell `LABL` |
| `CONNECTIVITY - FRAME` + `FRAME SECTION ASSIGNMENTS` + `FRAME SECTION PROPERTIES 01 - GENERAL` | beam domain (2026-09-03): parametric section from `Shape` + `t3 t2 tf tw` (Rectangular, Pipe, Box, I, Angle, Channel, Tee; Double Angle drawn as a Tee; unknown → RECT placeholder, counted in the summary); local 2 = SAP default, roll angles not read; beam ends get the joint displacements, no frame forces yet; one beam group per section (`sap`, `frameSection`) |
| `ELEMENT FORCES - AREA SHELLS` | per-corner `stress` components `F11 F22 F12 M11 M22 M12 V13 V23` (those present), unit `kip/ft` or `kip-ft/ft` |
| `JOINT DISPLACEMENTS` | `displacement` `Translation X/Y/Z`, `Rotation X/Y/Z` **in global** (rotated out of joint local axes, `Rz(A)·Ry(B)·Rx(C)`), fanned to every corner on the joint; optional `Translation R` / `T` |
| `OutputCase` | load cases, first-seen order, ids 1..N |
| `AREA SECTION ASSIGNMENTS` | one shell group per section name (`sap`, `section`) |
| `AREA SECTION PROPERTIES` | thickness goes into the section group's name, e.g. `WALL_T1 (0.500 in)` (`sap`, `section`, `thickness`); also drives the derived membrane stresses `S11 S22 S12 = F / t` (ksi for Kip/ft files) written after the forces |
| `JOINT RESTRAINT ASSIGNMENTS` | one node group per restraint pattern, e.g. `Restrained U1 U3` (`sap`, `restraint`) |
| `JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL` | node group `Local axes assigned` (`sap`, `localAxes`) |
| `GROUPS 2 - ASSIGNMENTS` | SAP groups (areas + joints + frames, `ALL` skipped) (`sap`, `group`) |

Groups carry no colour, so the viewer auto-assigns. Per the sidecar rule, nothing a user
*defines* goes in the binary. Not carried over from the Python: hydrostatic head, mesh
aspect ratio, normal-outward flag (continuous per-element "model" fields). Add them as
`kind: "model"` components through `AppendShellValues` if they are wanted.

## 4. One command: `run_sap.py` (config → SAP2000 → results → viewer)

```text
cd pythonTools/sap
python run_sap.py configs/example.toml            # build, run in SAP2000, export, open viewer
python run_sap.py configs/example.toml --no-run   # SAP already holds the analysed model
python run_sap.py configs/example.toml --no-viewer
```

Drives SAP2000 through its OAPI (COM, `comtypes` — `pip install comtypes`; `pywin32`
cannot bind SAP's interfaces). Attaches to the running SAP2000 or starts one;
`File.OpenFile` imports the `.s2k` (SAP accepts our GROUPS tables: 24 groups on the
example), saves the `.sdb`, `RunAnalysis`, then `Results.JointDispl` +
`Results.AreaForceShell` (full doubles — the `DatabaseTables` display tables are rounded)
are written as `results.s2k` in SAP's own table format, `SapToPluto` runs under PS 5.1, and
a local server on the repo root opens `viewer/index.html?bin=…&features=…` (the viewer
fetches both; 2026-09-03 addition). Everything lands in `models/<name>/` (gitignored).
`Results.JointDispl` reports in joint **local** axes, same as the table export, so the
arm's local→global rotation is still right. Module: `tankbuilder/sap_api.py`. Verified
end to end on the example 2026-09-03 (5 s import, 2 s run).

## 5. Tank generator (`build_tank.py`) — Python by design

```text
cd pythonTools/sap
python build_tank.py configs/example.toml        # -> models/example/example.s2k (gitignored)
python -m pytest -q                              # 20 tests, incl. the golden byte-compare
```

Refactored 2026-09-03 into the `tankbuilder` package: `spec.py` (constants, `TankSpec`,
TOML loader with typo rejection), `model.py` (`TankModel`: numbering, `base_joints` /
`top_joints` / `course_areas`, hydrostatics), `s2k.py` (one function per table group,
`s2k_text`, `write_s2k`, and `parse_s2k` to read tables back). `build_tank.py` is a thin
CLI. **SAP groups** (2026-09-03, `[groups]` in the config): `WALL`, `COURSE_01..`, `BASE_RING`,
`TOP_RING` are written as `GROUPS 1 - DEFINITIONS` + `GROUPS 2 - ASSIGNMENTS`; SAP re-exports
them and `SapToPluto` turns them into sidecar groups (verified on the example: 23 groups
through to the viewer readback). Not yet confirmed: SAP2000's importer accepting the two
GROUPS tables — check on the next import. **Contract:** `tests/golden/example.s2k` is the writer's output for `example.toml` (the writer
was byte-identical to the file SAP2000 v25 ran); it must stay byte-identical unless
regenerated on purpose.
**Configs:** `configs/example.toml` is the generic template; new configs and goldens are
gitignored (project dimensions stay local); `example.toml` is the only tracked config, a
**test fixture with generic dimensions** — never edit it toward a real tank, copy it to a new
(ignored) file.

`build_tank.py` + `configs/*.toml` (Python 3.11+, stdlib `tomllib`) generate the tank `.s2k` (wall mesh,
base restraints, local axes, hydrostatic joint pattern). It is a model *generator*, not a
Pluto arm, runs on the development side only, and needs a TOML parser (no C# 5 one in the
lib). Decision 2026-09-03: **stays Python** in `pythonTools/sap/` (SAP OAPI automation lives naturally there); port only if a production-side need appears, switching configs to JSON then.

## Related

[[vault/arms/Arms map|Arms map]] · [[vault/format/raw-viewer-writer|raw-viewer-writer]] ·
[[vault/format/features-sidecar|features-sidecar]] · [[vault/audits/sapviewer-audit|sapviewer-audit]]
