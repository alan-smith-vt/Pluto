---
title: Pluto handoff, current state
status: current
created: 2026-11-18
---

*↑ [[vault/Pluto Home|Home]] › [[vault/handoffs/Handoffs map|Handoffs map]]*

# Pluto handoff: current state

**Where it stands (2026-11-23, evening).** Branch `pipestress` holds:
- **The pipe workflow.** An Excel workbook or a TOML becomes one pipe model, reviewed in the viewer with CAESAR's error
  checks, and written as a CAESAR II `.cii`. An imported job comes back byte for byte.
- **The frame side.** A lossless SAP2000 `.s2k` model, edited in the frame workbook (3D View, live checks) and written
  to SAP and to STAAD.Pro (`-Std`, exact or refused). A `.std` it wrote reads back into a frame model, STAAD's edits
  too (`-In x.std`), through the STAAD lib's `.anl` reader, extended in place.
- **The CAESAR → SAP check model.** `Build-Frame -In <CAESAR job>` seeds the frame model from the pipe, for member
  forces and support reactions:
  - frames, bends, rigids, expansion joints, hangers and nozzles;
  - linear and nonlinear supports;
  - CAESAR's cases, each OPE continuing from SUS when a support is nonlinear.

  It is verified against CAESAR 15.01 and SAP 24.2 on two probes. The linear model agrees within 5% away from tees.
  The nonlinear one agrees except on friction-sized loads, as SAP's friction follows the load path.

**Read first:** [[vault/format/pipe-to-frame|pipe-to-frame]],
[[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]],
[[vault/format/sap-s2k-tables|sap-s2k-tables]], [[vault/arms/frame-workbook|frame-workbook]].

**Gaps in the check model:**
- **B31J's tee flexibility.** Tees are rigid joints, with a notice. The formulas are needed: B31J Table 1-1, or
  docs.hexagonppm.com allowed in the cloud environment.
- **Not yet run by a probe:**
  - an OPE with its own pressure set;
  - reducer stepping;
  - a finite expansion joint's weight;
  - k with flanged ends;
  - rotational gaps;
  - the hanger count.
- **Element forces** have not been compared with CAESAR's yet. Only restraint loads and displacements have.

**For the user to decide:** a personal Windows path is in the pushed history (fixed in the file on 2026-11-18).
Purging it means rewriting the branch's history.

**Waiting on the production machine:**
- STAAD's database form, both ways:
  - the PS 5.1 compile probe;
  - a `-Std` of SAP's library-probe export (its angle frame taken out), opened in STAAD.Pro 2025 to check it parses
    the database forms Pluto now writes;
  - the names probe's `.std` (its angle taken out) through `-In … -S2k`, imported into SAP.
- nothing for single angles: both ways confirmed end to end (STAAD's principal axes on SAP's to 0.0074°, SAP's on
  STAAD's to 0.025° with Pluto's I23);
  - whether `RA` gives SAP's leg arrangement (STAAD's `ST` is its mirror image), if drawings or STAAD's angle design
    matter.

**Seen, not settled:** SAP's own rows carry `IncludeSCAn=Yes` (shear center in the analysis) on its channel only, and
the angle probe showed SAP does not twist single angles. The `.std` notice says SAP twists tees and double angles too,
which was taken from the channel; no probe has run a tee or a double angle in SAP.

**Next:**
1. **Round 3, the sample job, in SAP only.** CAESAR's outputs are already in `scripts/caesar/testdata`: restraint loads,
   displacements and local element forces for all six cases. Seed it, run it in SAP and export.
   - The comparison adds element forces for the first time.
   - It exercises reducer stepping, expansion-joint weight, rotational gaps and two tees at full size.
   - Materials are Pluto's defaults (A53 B) unless the job's CAESAR workbook has a Materials sheet.
2. **B31J tee flexibility,** once its formulas are in hand.

**Later, not planned:** SAP back to CAESAR: results by CAESAR node and element, the support steel's stiffness into
CAESAR's restraints, re-seeding that keeps SAP-side additions, and writing a seeded model back as a `.cii`. See
"Possible later" in [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]].

**Branch `CalcFlow`** (0 behind `pipestress`; 2026-11-25). Steel connection calcs set up and run from one STAAD
`.anl` ([[vault/calcflow/CalcFlow map|CalcFlow map]]):
- **Built:** harvest and the calc library; the connection register and `plan`; `run` (Excel or LibreOffice, a record
  per check per revision) and `summary`; and the front door: `CalcFlow.xlsx` (Start links, Model Config per beam, Connection
  Lib marks, a schedule tab per template, Check and Run calcs) and the generated `Model_Data.xlsx`
  ([[vault/calcflow/calcflow-workbook|calcflow-workbook]]). Since 2026-11-24 also:
  - a template that takes a table of load rows: one calc per mark, its rows from only the beam ends that call it;
  - a template's input and result cells given by hand, and a block (anchor coordinates) typed as one table in a cell;
  - **Refresh config:** the workbook rebuilt around a changed model or templates folder, everything filled in carried
    over, the old file kept; post-process and check say when it is due;
  - a live check of the force units on Start; statuses in the soft colours of the pipe-review workbooks.
- **Decided 2026-11-24** ([[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]]): link cells,
  two files, a connection name is a mark with every geometry input on one schedule row, parser work on `pipestress`.
- **The frame `.anl` reader lives on `pipestress`** since 2026-11-24. It reads every usual output command: the model,
  member end forces, support reactions, joint and section displacements, the code check (three layouts, AISC 360-05
  to 360-22 and the older ASD and LRFD codes) and the printed member tables, checked on the probe's real `.anl`
  (STAAD.Pro 2025). Model_Data.xlsx tabulates all of it.
- **Waiting on the production machine:** `CalcFlow.ps1 new` with the real templates; the link cells, the refresh
  waiting for Excel to close the workbook, and the Force units formulas in Excel;
  `scripts\calcflow\tests\Run-Tests.ps1` under Windows PowerShell 5.1 (the Excel stage's first run); a template built
  on an Excel data table (it needs the Excel engine).
- **Run end to end on 2026-11-25** on a made-up frame (8 marks, 26 calcs, LibreOffice): the DCRs agree with the
  templates' reference code to 5e-13. Found and fixed: harvest chose several governing results; sign-led text was
  read as formulas. Found and open: the circular weld tab needs Excel 365; an anchorage calc takes the column's end
  forces, not the support reaction; harvest lists helper cells off the print area as inputs.
- **Next:** the same pass on the production machine with Excel; a support reaction as a calc's demand; the first real
  project through the workbook; section dimensions looked up from the shape.

Last sessions: [[vault/handoffs/handoff-2026-11-25|handoff-2026-11-25]] (`CalcFlow`), [[vault/handoffs/handoff-2026-11-24|handoff-2026-11-24]] (`CalcFlow`), [[vault/handoffs/handoff-2026-11-23|handoff-2026-11-23]] (`pipestress`).
