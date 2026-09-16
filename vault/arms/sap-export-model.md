---
title: SAP2000 open model → viewer (export_model.py)
status: current
created: 2026-09-15
---

# SAP2000 open model → viewer — `export_model.py`

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Any model SAP2000 holds → `<outBase>.bin` + `<outBase>.features.json`, with no config and no
manual `.s2k` export. `pythonTools/sap/export_model.py`; the OAPI driver is
[[vault/arms/sap-tank-builder|sap-tank-builder]]'s `tankbuilder/sap_api.py`, the arm is
[[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]]'s `SapToPluto`. Written 2026-09-15 for the
<project> tank (Notes vault), an auto-meshed prestressed concrete tank.

```text
python export_model.py                                    # model open in SAP -> ~/Downloads/<name>.bin
python export_model.py -o D:\proj\viewer\Model            # output base, no extension
python export_model.py "D:\proj\Model.sdb"                # open that file in the running SAP first
python export_model.py "D:\proj\Model.sdb" --new-instance # ... in a second SAP; the first is untouched
python export_model.py --run | --no-results | --cylindrical | --view | --keep-open
```

## What it does

- **Attach.** `SapSession.attach_or_start`, or a second SAP with `--new-instance` (closed at the
  end unless `--keep-open`). `GetActiveObject` only ever reaches the *first* SAP started, so with
  two open the attach goes to the older one; use `--new-instance` when the running SAP holds
  something else.
- **Run** only when a case has no results (or `--run`). SAP *saves the model file when it runs*, so
  the model is first saved as `<outBase>.sdb` (a working copy, same convention as
  `Run-SapRelease.ps1`) and every case is flagged to run (`SetRunCaseFlag`; files arrive with
  cases set "do not run"). The file SAP opened is never written. Cases that still do not finish
  are listed with the first warning from `Analysis Messages` (e.g. staged construction:
  *Requires Ultimate license*) and simply have no results.
- **Model = the analysis mesh**, not the objects: `PointElm` / `AreaElm` / `LineElm`, renumbered
  1..N (the arm wants integer labels; real models have `R751.4 T0.0 Z0.0`), coordinates from
  `PointElm.GetCoordCartesian` (full precision; `DatabaseTables` is display-rounded), section
  and local-axis angle per element, restraints and SAP groups carried down from the objects
  (`AreaElm.GetObj`, `LineElm.GetObj`, `PointObj.GetElm`; group `ALL` skipped). Frame and area
  section-property tables come from `DatabaseTables` verbatim. Line elements of type ≠ 0
  (tendons, cables) are skipped and counted. `<outBase>.labels.csv` maps every id to its element
  and object name. An unmeshed model (the tank builder's) comes out one-to-one with its objects.
- **Results** streamed straight from `Results.JointDispl / AreaForceShell / AreaStressShell /
  FrameForce` on group `ALL` (GroupElm) into `<outBase>.results.s2k`, names remapped through the
  mesh ids, rows for skipped elements dropped. Output is step-by-step (`SetOptionNLStatic(2)`);
  a case with several saved steps (staged construction) becomes **one viewer case per step,
  `<case>.<n>`**, or only its last step under the bare name with `--final-only`. Option 1
  ("last step") is not usable here: for staged cases it returns Max / Min envelope rows, two per
  joint, principals zeroed.
- **Export** through `SapToPluto` under PS 5.1, then the headless check is
  `node viewer/tests/readback_sap.js <outBase>`.

## Tank 1 (first run, SAP 26.3.0)

1869 joints / 908 areas / 300 frames as objects → 49 626 points, 18 275 shells, 6 416 frame
elements (19 876 tendon segments skipped). 8 linear cases in 65 s; the three staged cases need
the Ultimate licence (added the same day) and run in 250 s with 9 saved stages each, so the
viewer lists 35 cases. `results.s2k` 3.5 GB, `.bin` 413 MB, mesh walk 8 s, readback passed.
`DatabaseTables.GetTableForDisplayArray` on an empty table returns the *previous* table's
buffer with `None` field names — guarded in `table_rows`.

## Not carried

Tendons, cables, links, solids (no viewer domain); frame end releases and explicit insertion
offsets (cardinal points are expanded per element); reactions and link forces
([[vault/arms/sap-results-coverage|sap-results-coverage]]). Frame stresses need `Area / S33 / S22`
on the section row, which `General` sections lack (NaN).
